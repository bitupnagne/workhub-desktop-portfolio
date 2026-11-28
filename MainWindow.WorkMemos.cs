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

// [W6] MainWindow 분리 — 자료전송 탭(목록/검색/즐겨찾기/작성). 동작 변경 없음(파일 분리만).
public partial class MainWindow
{
    private async Task LoadWorkMemosAsync()
    {
        try
        {
            var data = await _apiService.GetWorkMemosAsync(_workMemoBox, _workMemoQuery);
            if (data == null) { _workMemos = new List<WorkMemoModel>(); if (lstWorkMemos != null) lstWorkMemos.ItemsSource = _workMemos; return; }
            var root = data.Value;
            var list = new List<WorkMemoModel>();
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
            foreach (var m in items.EnumerateArray())
            {
                long senderUserId = m.TryGetProperty("senderUserId", out var su) ? su.GetInt64() : 0;
                var memo = new WorkMemoModel
                {
                    Id = m.GetProperty("id").GetInt64(),
                    SenderUserId = senderUserId,
                    SenderName = m.TryGetProperty("senderName", out var sn) && sn.ValueKind == JsonValueKind.String ? sn.GetString() : "",
                    Title = m.TryGetProperty("title", out var t) ? t.GetString() : "",
                    Body = m.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "",
                    Importance = m.TryGetProperty("importance", out var imp) ? imp.GetString() : "NORMAL",
                    SentAt = m.TryGetProperty("sentAt", out var sa) && sa.ValueKind == JsonValueKind.String && DateTime.TryParse(sa.GetString(), out var dt) ? dt : null,
                    IsStarred = m.TryGetProperty("starred", out var sv) && sv.ValueKind == JsonValueKind.True,
                    ReadAt = m.TryGetProperty("readAt", out var ra) && ra.ValueKind == JsonValueKind.String && DateTime.TryParse(ra.GetString(), out var rdt) ? rdt : null,
                    IsSenderSelf = senderUserId == _myUserId,
                    Attachments = ParseMemoAttachments(m)
                };
                list.Add(memo);
            }
        }
            _workMemos = list;
            if (lstWorkMemos != null) lstWorkMemos.ItemsSource = _workMemos;
            UpdateWorkMemoBadgeFromPayload(root);
            UpdateWorkMemoFilterVisuals();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LoadMemos] {ex}"); }
    }

    private List<WorkMemoAttachmentModel> ParseMemoAttachments(JsonElement memo)
    {
        var list = new List<WorkMemoAttachmentModel>();
        if (!memo.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in attachments.EnumerateArray())
        {
            list.Add(new WorkMemoAttachmentModel
            {
                Id = a.GetProperty("id").GetInt64(),
                MemoId = a.TryGetProperty("memoId", out var mi) ? mi.GetInt64() : 0,
                OriginalFilename = a.TryGetProperty("originalFilename", out var of) ? of.GetString() : "",
                ContentType = a.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() : null,
                FileSize = a.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : 0,
                AttachmentType = a.TryGetProperty("attachmentType", out var at) ? at.GetString() : "FILE"
            });
        }
        return list;
    }

    private void UpdateWorkMemoBadgeFromPayload(JsonElement root)
    {
        int unread = root.TryGetProperty("unreadReceived", out var ur) && ur.ValueKind == JsonValueKind.Number ? ur.GetInt32() : 0;
        ShowWorkMemoBadge(unread);
    }

    private void ShowWorkMemoBadge(int count)
    {
        if (count <= 0) { badgeWorkMemo.Visibility = Visibility.Collapsed; return; }
        badgeWorkMemo.Visibility = Visibility.Visible;
        txtWorkMemoBadge.Text = count > 99 ? "99+" : count.ToString();
    }

    private void UpdateWorkMemoFilterVisuals()
    {
        SetChipActive(btnWmFilterReceived, _workMemoBox == "received");
        SetChipActive(btnWmFilterSent, _workMemoBox == "sent");
        SetChipActive(btnWmFilterStarred, _workMemoBox == "starred");
    }

    private void SetChipActive(System.Windows.Controls.Button? btn, bool active)
    {
        if (btn == null) return;
        try
        {
            btn.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, active ? "BrandBrush" : "SurfaceBrush");
            if (active) { btn.Foreground = Brushes.White; }
            else { btn.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, "Text2Brush"); }
            btn.SetResourceReference(System.Windows.Controls.Button.BorderBrushProperty, active ? "BrandBrush" : "BorderBrush");
        }
        catch { }
    }

    private async void WorkMemoFilter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string box)
            {
                _workMemoBox = box;
                await LoadWorkMemosAsync();
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WorkMemoFilter] {ex}"); }
    }

    private void WorkMemoSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _workMemoSearchDebouncer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _workMemoSearchDebouncer.Stop();
        _workMemoSearchDebouncer.Tick -= WorkMemoSearchDebouncerTick;
        _workMemoSearchDebouncer.Tick += WorkMemoSearchDebouncerTick;
        _workMemoSearchDebouncer.Start();
    }

    private async void WorkMemoSearchDebouncerTick(object? sender, EventArgs e)
    {
        _workMemoSearchDebouncer?.Stop();
        _workMemoQuery = txtWorkMemoSearch.Text?.Trim();
        await LoadWorkMemosAsync();
    }

    private async void WorkMemoRow_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (lstWorkMemos.SelectedItem is not WorkMemoModel memo) return;
            e.Handled = true;
            memo.ReadAt = DateTime.Now; // 낙관적 표시 — 서버 markRead는 detail에서 처리
            await ShowWorkMemoDetailAsync(memo.Id);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WorkMemoRow] {ex}"); }
    }

    private async void WorkMemoCardStar_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not long memoId) return;
        var memo = _workMemos.FirstOrDefault(m => m.Id == memoId);
        if (memo == null) return;
        if (memo.IsStarred)
        {
            bool ok = await ShowCustomModal("즐겨찾기 해제", "이 업무전달을 즐겨찾기에서 해제할까요?", true);
            if (!ok) return;
        }
        try
        {
            bool now = await _apiService.ToggleWorkMemoStarAsync(memoId);
            memo.IsStarred = now;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[StarToggle] {ex}"); }
    }

    private async Task ShowWorkMemoDetailAsync(long memoId)
    {
        if (_workMemoDetailWindow != null)
        {
            await _workMemoDetailWindow.LoadAsync(memoId);
            _workMemoDetailWindow.Activate();
            return;
        }
        var win = new WorkMemoDetailWindow(_apiService) { Owner = this };
        PositionDockedWindow(win);
        win.Closed += (_, _) => { if (ReferenceEquals(_workMemoDetailWindow, win)) _workMemoDetailWindow = null; };
        _workMemoDetailWindow = win;
        win.Show();
        await win.LoadAsync(memoId);
    }

    private void ComposeWorkMemo_Click(object sender, RoutedEventArgs e)
    {
        try { OpenWorkMemoCompose(null, null); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Compose] {ex}"); }
    }

    public void OpenWorkMemoCompose(string? preBody, IEnumerable<string>? preAttachments)
    {
        try
        {
            var win = new WorkMemoComposeWindow(_apiService, _allEmployees ?? new List<EmployeeModel>(), _fullOrgTree ?? new List<OrgNodeModel>(), _myUserId, preBody, preAttachments) { Owner = this };
            win.Sent += async () =>
            {
                await LoadWorkMemosAsync();
                await RefreshWorkMemoBadgeAsync();
            };
            PositionDockedWindow(win);
            win.Show();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OpenCompose] {ex}"); }
    }

    private bool _workMemoLoadedOnce;
    private async void TabWorkMemo_OnSelected(object sender, RoutedEventArgs e)
    {
        if (e.Source != sender) return; // 자식 이벤트 무시
        try
        {
            // TabItem.Content 가 막 realize 되는 시점이라 named 요소 접근 전에 한 박자 양보.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            // [1.0.0] 탭 진입 시 항상 수신함부터 — 사용자가 다른 박스(발신/즐겨)에 머물러 있던 상태 리셋.
            _workMemoBox = "received";
            if (!_workMemoLoadedOnce) _workMemoLoadedOnce = true;
            UpdateWorkMemoFilterVisuals();
            await LoadWorkMemosAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WorkMemoTab] {ex}"); }
    }

    private async Task RefreshWorkMemoBadgeAsync()
    {
        try { int n = await _apiService.GetUnreadWorkMemoCountAsync(); ShowWorkMemoBadge(n); }
        catch { }
    }

}
