using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Web.WebView2.Core;

namespace Portfolio.OfficeSystem;

// [W6] MainWindow 분리 — 알림(목록/그룹/필터·토스트·배지). 동작 변경 없음(파일 분리만).
public partial class MainWindow
{
    private void ToggleNotificationsPopup_Click(object sender, RoutedEventArgs e)
    {
        if (popupNotifications == null) return;
        popupNotifications.IsOpen = !popupNotifications.IsOpen;
    }

    private string _notifFilter = "unread";
    private List<NotificationModel> _allNotifications = new();

    private void NotifFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string filter)
        {
            _notifFilter = filter;
            ApplyNotificationFilter();
        }
    }

    private void ApplyNotificationFilter()
    {
        bool unread = _notifFilter == "unread";
        var filtered = unread
            ? _allNotifications.Where(n => n.IsUnread).ToList()
            : _allNotifications.Where(n => !n.IsUnread).ToList();
        var groups = BuildNotificationGroups(filtered);
        lstNotifications.ItemsSource = null;
        lstNotifications.ItemsSource = groups;
        emptyNotifications.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        txtEmptyNotifications.Text = unread ? "새 알림이 없어요" : "읽은 알림이 없어요";

        int newCount = _allNotifications.Count(n => n.IsUnread);
        txtNotifNewCount.Text = newCount > 0 ? $" {newCount}" : "";
        SetNotifChipActive(btnNotifFilterNew, unread);
        SetNotifChipActive(btnNotifFilterRead, !unread);

        // 업무전달 사이드바 뱃지 — 탭 클릭 전이라도 알림에서 즉시 계산
        int workMemoUnread = _allNotifications.Count(n =>
            string.Equals(n.Type, "WORK_MEMO", System.StringComparison.OrdinalIgnoreCase) && n.IsUnread);
        ShowWorkMemoBadge(workMemoUnread);
    }

    private void SetNotifChipActive(System.Windows.Controls.Button? btn, bool active)
    {
        if (btn == null) return;
        btn.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, active ? "BrandBrush" : "SurfaceBrush");
        if (active) { btn.Foreground = System.Windows.Media.Brushes.White; }
        else { btn.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, "Text2Brush"); }
        btn.SetResourceReference(System.Windows.Controls.Button.BorderBrushProperty, active ? "BrandBrush" : "BorderBrush");
    }

    private void NotificationGroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NotificationGroup group) return;

        // 단일 알림: 즉시 열기
        if (!group.IsGroup) { _ = OpenNotificationAsync(group.Head); return; }

        // 그룹: 단일 클릭=펼치기(지연), 더블 클릭=채팅방 입장.
        // MouseLeftButtonUp 은 ClickCount 가 불안정하므로 타이머로 더블클릭을 감지한다.
        if (_notifClickTimer == null)
        {
            _notifClickTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _notifClickTimer.Tick += (s, ev) =>
            {
                _notifClickTimer!.Stop();
                if (_pendingNotifGroup != null)
                {
                    _pendingNotifGroup.IsExpanded = !_pendingNotifGroup.IsExpanded;
                    _pendingNotifGroup = null;
                }
            };
        }

        if (_notifClickTimer.IsEnabled && ReferenceEquals(_pendingNotifGroup, group))
        {
            // 250ms 내 같은 그룹 두 번째 클릭 → 더블클릭 → 방 입장
            _notifClickTimer.Stop();
            _pendingNotifGroup = null;
            _ = OpenNotificationAsync(group.Head);
        }
        else
        {
            _pendingNotifGroup = group;
            _notifClickTimer.Stop();
            _notifClickTimer.Start();
        }
    }

    private static List<NotificationGroup> BuildNotificationGroups(IEnumerable<NotificationModel> notifications)
    {
        var groups = notifications
            .GroupBy(n =>
                string.Equals(n.Type, "MESSAGE", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(n.LinkUrl)
                    ? "msg:" + n.LinkUrl
                    : "solo:" + n.Id)
            .Select(g => new NotificationGroup
            {
                LinkUrl = g.First().LinkUrl,
                Items = g.OrderByDescending(n => n.Id).ToList()
            })
            .OrderByDescending(g => g.Head.IsUnread)
            .ThenByDescending(g => g.Head.Id)
            .ToList();

        ApplyNotificationSectionTitles(groups);
        return groups;
    }

    private static void ApplyNotificationSectionTitles(List<NotificationGroup> groups)
    {
        foreach (var group in groups) group.SectionTitle = null;

        var firstUnread = groups.FirstOrDefault(g => g.Head.IsUnread);
        if (firstUnread != null) firstUnread.SectionTitle = "새로운 알림";

        var firstRead = groups.FirstOrDefault(g => !g.Head.IsUnread);
        if (firstRead != null) firstRead.SectionTitle = "읽은 알림";
    }

    /* [W9] 알림 작업 로딩 — 메인 앱 오버레이가 아니라 알림 창 안에서 표시한다. */
    private void ShowNotifBusy(string text, int? total = null)
    {
        txtNotifBusy.Text = text;
        if (total.HasValue)
        {
            prgNotifBusy.IsIndeterminate = false;
            prgNotifBusy.Minimum = 0;
            prgNotifBusy.Maximum = total.Value;
            prgNotifBusy.Value = 0;
        }
        else
        {
            prgNotifBusy.IsIndeterminate = true;
        }
        notifBusyOverlay.Visibility = Visibility.Visible;
    }

    private void HideNotifBusy()
    {
        notifBusyOverlay.Visibility = Visibility.Collapsed;
        prgNotifBusy.IsIndeterminate = true;
    }

    /** [W9] 알림 모두 읽음 — 웹 우측 패널과 동일 기능. */
    private async void NotifReadAll_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        btnNotifReadAll.IsEnabled = false;
        ShowNotifBusy("모두 읽음 처리 중...");
        try { await _apiService.MarkAllNotificationsReadAsync(); }
        catch { }
        finally
        {
            HideNotifBusy();
            btnNotifReadAll.IsEnabled = true;
            RefreshPanelData();
        }
    }

    private async void DeleteNotificationGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is NotificationGroup group)
        {
            e.Handled = true;
            var items = group.Items.ToList();
            int total = items.Count;
            if (total == 0) return;

            int done = 0;
            // [W9] 알림 창 안 로딩바로 진행 표시(기존: 메인 앱 전체 오버레이 → 어색)
            ShowNotifBusy($"알림 삭제 중 (0/{total})", total);

            var tasks = items.Select(item => Task.Run(async () =>
            {
                try { await _apiService.DeleteNotificationAsync(item.Id); }
                catch { }
                int n = System.Threading.Interlocked.Increment(ref done);
                Dispatcher.Invoke(() =>
                {
                    txtNotifBusy.Text = $"알림 삭제 중 ({n}/{total})";
                    prgNotifBusy.Value = n;
                });
            })).ToArray();

            try { await Task.WhenAll(tasks); }
            finally
            {
                HideNotifBusy();
                RefreshPanelData();
            }
        }
    }

    private void UpdateBadge(System.Windows.Controls.Border badge, TextBlock text, int count)
    {
        if (count <= 0) { badge.Visibility = Visibility.Collapsed; return; }
        text.Text = count > 99 ? "99+" : count.ToString();
        badge.Visibility = Visibility.Visible;
    }

    private async void DeleteNotification_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is long id)
        {
            if (await ShowCustomModal("확인", "알림을 삭제하시겠습니까?", true)) {
                await _apiService.DeleteNotificationAsync(id);
                RefreshPanelData();
            }
        }
    }

    private async void NotificationItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
        if (sender is FrameworkElement element && element.DataContext is NotificationModel notification)
            await OpenNotificationAsync(notification);
    }

    private async Task OpenNotificationAsync(NotificationModel notification)
    {
        if (popupNotifications != null) popupNotifications.IsOpen = false;
        await _apiService.MarkNotificationReadAsync(notification.Id);
        RefreshPanelData();

        // 1) 메시지 알림은 윈도우 채팅방을 직접 연다.
        var roomId = ExtractMessageRoomId(notification.LinkUrl);
        if (roomId.HasValue)
        {
            string? title = CleanMessageTitle(notification.Title);
            OpenChatWindow(roomId.Value, title);
            return;
        }

        // 1-b) 업무전달 알림은 상세 창을 직접 연다.
        var memoId = ExtractWorkMemoId(notification.LinkUrl, notification.Type);
        if (memoId.HasValue)
        {
            await ShowWorkMemoDetailAsync(memoId.Value);
            return;
        }

        // 2) 공지사항·배포시스템(배포/MR) 알림은 WebView2 가 아니라 기본 브라우저 새 창(SSO)으로 연다.
        bool isNotice = string.Equals(notification.Type, "ADMIN_NOTICE", StringComparison.OrdinalIgnoreCase)
                        || (notification.LinkUrl?.StartsWith("/notices/", StringComparison.OrdinalIgnoreCase) ?? false);
        bool isDeploySystem = (notification.Type?.StartsWith("DEPLOY_", StringComparison.OrdinalIgnoreCase) ?? false)
                        || (notification.Type?.StartsWith("MR_", StringComparison.OrdinalIgnoreCase) ?? false)
                        || (notification.LinkUrl?.Contains("/deploy-system/", StringComparison.OrdinalIgnoreCase) ?? false);

        var redirect = string.IsNullOrWhiteSpace(notification.LinkUrl) ? "/notifications" : notification.LinkUrl!;
        if (isDeploySystem) redirect = BuildDeploySystemRedirect(redirect);
        var url = await _apiService.CreateSsoUrlAsync(redirect);
        if (string.IsNullOrEmpty(url)) { await ShowCustomModal("오류", "알림 연결 화면을 열 수 없습니다.", false); return; }

        if (isNotice || isDeploySystem)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await ShowCustomModal("오류", "기본 브라우저를 열 수 없습니다: " + ex.Message, false);
            }
            return;
        }

        // 3) 결재 문서와 그 외 화면은 WebView2 팝업으로 연다.
        Dispatcher.Invoke(() =>
        {
            var docWin = new DocumentWindow(url, notification.Title ?? "문서 보기");
            docWin.Show();
        });
    }

    /// <summary>
    /// 배포시스템 알림의 링크를 오피스의 배포 SSO 브리지로 감싼다.
    ///
    /// 배포시스템이 보내는 linkUrl 은 "/deploy/request-detail?id=12" 처럼 <b>배포시스템 기준
    /// 경로</b>다. 이걸 그대로 데스크톱 SSO 의 redirect 로 넘기면, 오피스가 자기 세션을 세운 뒤
    /// <b>오피스 서버의</b> 같은 경로로 이동해 404 가 난다 — 오피스에는 /deploy/** 라우트가 없다.
    ///
    /// 오피스 웹(app.html 의 시스템 이동 버튼)은 /deploy-system/sso 를 경유하는데 데스크톱만
    /// 그 경유를 빠뜨리고 있었다. 같은 브리지를 타도록 맞춘다.
    ///
    /// 이미 브리지 경로면 다시 감싸지 않는다(중복 감싸기 방지).
    /// </summary>
    private static string BuildDeploySystemRedirect(string linkUrl)
    {
        if (linkUrl.StartsWith("/deploy-system/sso", StringComparison.OrdinalIgnoreCase)) return linkUrl;
        if (!linkUrl.StartsWith("/", StringComparison.Ordinal) || linkUrl.StartsWith("//", StringComparison.Ordinal))
        {
            // 배포시스템 기준 경로가 아니면 브리지가 기본값(대시보드)으로 보낸다.
            return "/deploy-system/sso";
        }
        return "/deploy-system/sso?redirect=" + Uri.EscapeDataString(linkUrl);
    }

    private static long? ExtractMessageRoomId(string? linkUrl)
    {
        if (string.IsNullOrEmpty(linkUrl)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(linkUrl, @"messageRoomId=(\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var id)) return id;
        return null;
    }

    private static long? ExtractWorkMemoId(string? linkUrl, string? type)
    {
        if (string.IsNullOrEmpty(linkUrl)) return null;
        if (!string.Equals(type, "WORK_MEMO", StringComparison.OrdinalIgnoreCase)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(linkUrl, @"/work-memos/(\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var id)) return id;
        return null;
    }

    private Dictionary<string, string> ParseToastArguments(string argument)
    {
        return argument.Split(new[] { '&', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));
    }

    private static bool IsMessageNotification(NotificationModel notification)
    {
        return string.Equals(notification.Type, "MESSAGE", StringComparison.OrdinalIgnoreCase)
            || ExtractMessageRoomId(notification.LinkUrl).HasValue;
    }

    private bool ShouldDisplayNotification(NotificationModel notification)
    {
        if (!IsMessageNotification(notification))
        {
            return true;
        }

        if (!notification.IsUnread)
        {
            return false;
        }

        var roomId = ExtractMessageRoomId(notification.LinkUrl);
        if (!roomId.HasValue || !IsChatRoomFocused(roomId.Value))
        {
            return true;
        }

        MarkRoomReadInMessageList(roomId.Value);
        MarkRoomNotificationsReadLocally(roomId.Value);
        _ = _apiService.MarkRoomNotificationsReadAsync(roomId.Value);
        return false;
    }

    private bool IsWorkMemoNotification(NotificationModel n) =>
        string.Equals(n.Type, "WORK_MEMO", StringComparison.OrdinalIgnoreCase);

    private void ShowToast(NotificationModel notification)
    {
        // [1.0.0] 자료전송 알림은 메시지처럼 우리 NotificationPopupWindow 로 띄우고,
        //         클릭 시 OpenNotificationAsync → ExtractWorkMemoId → WorkMemoDetailWindow 가 열림.
        if (IsWorkMemoNotification(notification))
        {
            var title = notification.Title ?? "자료전송";
            var sender = notification.SenderName ?? "";
            var body = notification.PlainBody ?? notification.Body ?? "";
            NotificationPopupWindow.ShowMessage(title, sender, body, () => _ = OpenNotificationAsync(notification), null);
            return;
        }
        if (!IsMessageNotification(notification))
        {
            Dispatcher.Invoke(() => {
                try {
                    new ToastContentBuilder()
                        .AddArgument("notificationId", notification.Id.ToString())
                        .AddArgument("link", notification.LinkUrl ?? "/notifications")
                        .AddText(notification.Title ?? "WorkHub Desktop")
                        .AddText(notification.PlainBody ?? notification.Body ?? "").Show();
                } catch { }
            });
            return;
        }
        // 메시지 알림은 해당 채팅방을 보고 있으면 알림 목록과 팝업에서 제외한다.
        var rid = ExtractMessageRoomId(notification.LinkUrl);
        if (rid.HasValue && IsChatRoomFocused(rid.Value))
        {
            MarkRoomReadInMessageList(rid.Value);
            MarkRoomNotificationsReadLocally(rid.Value);
            _ = _apiService.MarkRoomNotificationsReadAsync(rid.Value);
            return;
        }

        ShowMessagePopup(notification);
    }

    private static string CleanMessageTitle(string? title)
    {
        var clean = title?
            .Replace("[새 메시지]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[메시지]", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return string.IsNullOrWhiteSpace(clean) ? "새 메시지" : clean!;
    }

    private void ShowMessagePopup(NotificationModel notification)
    {
        var title = CleanMessageTitle(notification.Title);
        var sender = string.IsNullOrWhiteSpace(notification.SenderName) ? "메시지" : notification.SenderName!;
        var body = notification.PlainBody ?? notification.Body ?? "";
        var roomId = ExtractMessageRoomId(notification.LinkUrl);
        NotificationPopupWindow.ShowMessage(title, sender, body, () => _ = OpenNotificationAsync(notification), roomId);
    }

}
