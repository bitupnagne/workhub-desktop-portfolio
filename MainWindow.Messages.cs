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

// [W6] MainWindow 분리 — 메시지(방 목록 동기화·채팅 창·초대/생성·컨텍스트 메뉴). 동작 변경 없음(파일 분리만).
public partial class MainWindow
{
    private void RemoveRoomFromMessageList(long roomId)
    {
        var removed = _rooms.FirstOrDefault(r => r.Id == roomId);
        if (removed == null) return;
        _rooms.Remove(removed);
        emptyMessages.Visibility = _rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge(badgeMessages, txtMessagesBadge, _rooms.Sum(r => r.UnreadCount));
    }

    private void MarkRoomReadInMessageList(long roomId)
    {
        var room = _rooms.FirstOrDefault(r => r.Id == roomId);
        if (room == null || room.UnreadCount == 0) return;
        room.UnreadCount = 0;
        UpdateBadge(badgeMessages, txtMessagesBadge, _rooms.Sum(r => r.UnreadCount));
    }

    private void ApplyImmediateRoomPreview(System.Text.Json.JsonElement payload)
    {
        // 패널 새로고침이 도착하기 전에, MESSAGE_CREATED 페이로드의 body 만으로
        // 해당 룸의 카드 미리보기를 즉시 갱신해서 발신 직후 빈 줄로 보이는 현상을 막는다.
        try
        {
            if (!payload.TryGetProperty("roomId", out var ridProp)) return;
            long roomId = ridProp.GetInt64();
            if (!payload.TryGetProperty("message", out var msg)) return;
            string body = msg.TryGetProperty("body", out var bp) && bp.ValueKind == System.Text.Json.JsonValueKind.String
                ? (bp.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(body))
            {
                // 본문이 비어있고 첨부가 있는 경우 — 채팅 통일된 라벨로 즉시 표시
                bool hasImage = false; bool hasAny = false;
                if (msg.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var a in attachments.EnumerateArray())
                    {
                        hasAny = true;
                        if (a.TryGetProperty("attachmentType", out var at) && at.ValueKind == System.Text.Json.JsonValueKind.String
                            && string.Equals(at.GetString(), "IMAGE", StringComparison.OrdinalIgnoreCase)) hasImage = true;
                    }
                }
                if (hasImage) body = "이미지를 보냈습니다";
                else if (hasAny) body = "파일을 보냈습니다";
            }
            var room = _rooms.FirstOrDefault(r => r.Id == roomId);
            if (room == null) return;
            room.LastMessageBody = body;
            // 발신자 본인의 메시지가 아니라면 unread 도 +1 — 패널 새로고침이 정정해줌
            long senderId = msg.TryGetProperty("senderUserId", out var sp) && sp.ValueKind == System.Text.Json.JsonValueKind.Number ? sp.GetInt64() : 0;
            if (senderId != _myUserId) room.UnreadCount = room.UnreadCount + 1;
            // 룸을 상단으로 끌어올림 (pinned 룸들 뒤지만 단순하게 처리)
            int cur = _rooms.IndexOf(room);
            if (cur > 0) _rooms.Move(cur, 0);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ImmediatePreview] {ex}"); }
    }

    private void MergeRooms(List<MessageRoomModel> incoming)
    {
        // 1.0.76 의 surgical Move 머지는 일부 환경에서 ObservableCollection.Move 후
        // ItemTemplate 컨테이너가 표시되지 않는 케이스가 있었다(최근 발신 룸/오픈 룸).
        // 안전한 방식 — 기존 인스턴스는 in-place Apply 로 유지(스크롤/선택 보존),
        // 순서/추가/삭제는 Clear + 재추가로 명확히 적용.
        if (!_roomsBound)
        {
            lstMessages.ItemsSource = _rooms;
            _roomsBound = true;
        }
        var existingById = _rooms.ToDictionary(r => r.Id);
        foreach (var inc in incoming)
        {
            if (existingById.TryGetValue(inc.Id, out var ex))
            {
                ex.Apply(inc);
            }
        }
        var nextList = incoming
            .Select(inc => existingById.TryGetValue(inc.Id, out var ex) ? ex : inc)
            .ToList();
        _rooms.Clear();
        foreach (var item in nextList) _rooms.Add(item);
    }

    private void MarkRoomNotificationsReadLocally(long roomId)
    {
        if (lstNotifications.ItemsSource is not IEnumerable<NotificationGroup> source) return;
        var groups = source.ToList();
        var changed = false;

        foreach (var group in groups.ToList())
        {
            var before = group.Items.Count;
            group.Items = group.Items
                .Where(notification =>
                    !string.Equals(notification.Type, "MESSAGE", StringComparison.OrdinalIgnoreCase)
                    || ExtractMessageRoomId(notification.LinkUrl) != roomId)
                .ToList();
            changed |= before != group.Items.Count;
        }

        groups = groups.Where(g => g.Items.Count > 0).ToList();
        ApplyNotificationSectionTitles(groups);

        if (!changed) return;
        lstNotifications.ItemsSource = null;
        lstNotifications.ItemsSource = groups;
        UpdateBadge(badgeNotifications, txtNotificationsBadge, groups.SelectMany(g => g.Items).Count(n => n.IsUnread));
        emptyNotifications.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // 직원 상세창/채팅 참여자/아바타의 '1:1 대화' 요청 → 1:1 방 열기(기존 방 있으면 재사용)
    private async void OpenDirectChat(long userId, string? name)
    {
        ShowLoading("대화방 생성 중 (0/1)");
        var roomId = await _apiService.OpenDirectRoomAsync(userId);
        txtLoadingStatus.Text = "대화방 생성 중 (1/1)";
        HideLoading();
        if (roomId.HasValue) OpenChatWindow(roomId.Value, string.IsNullOrEmpty(name) ? null : name + "님과의 대화");
    }

    private void ShowCreateRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCreateRoomPicker != null && _activeCreateRoomPicker.IsVisible) { _activeCreateRoomPicker.Activate(); return; }
        var win = new MemberPickerWindow("새 대화", "생성", _allEmployees, _fullOrgTree, _myUserId) { Owner = this };
        _activeCreateRoomPicker = win;
        // Show + 이벤트 — ShowDialog 가 다른 창(채팅창 등) 입력을 막던 문제 회피.
        win.Confirmed += ids => CreateRoomFromIds(ids);
        win.Closed += (_, _) => { if (ReferenceEquals(_activeCreateRoomPicker, win)) _activeCreateRoomPicker = null; };
        win.Show();
    }

    private async void CreateRoomFromIds(List<long> userIds)
    {
        var recipients = userIds
            .Select(id => _allEmployees.FirstOrDefault(emp => emp.UserId == id))
            .Where(emp => emp != null && emp!.UserId != _myUserId)
            .Select(emp => emp!)
            .GroupBy(emp => emp.UserId)
            .Select(g => g.First())
            .ToList();

        if (recipients.Count == 0) { await ShowCustomModal("알림", "본인 외 대화 상대를 한 명 이상 선택하세요.", false); return; }
        ShowLoading("방 생성 중 (0/1)");

        long? roomId;
        if (recipients.Count == 1)
        {
            roomId = await _apiService.OpenDirectRoomAsync(recipients[0].UserId);
        }
        else
        {
            string title = string.Join(", ", recipients.Select(emp => emp.Name).Take(3));
            if (recipients.Count > 3) title += $" 외 {recipients.Count - 3}명";
            roomId = await _apiService.CreateRoomAsync(title, recipients.Select(emp => emp.UserId).ToList());
        }

        txtLoadingStatus.Text = "방 생성 중 (1/1)";
        HideLoading();
        if (roomId.HasValue) { RefreshPanelData(); OpenChatWindow(roomId.Value, null); }
        else { await ShowCustomModal("오류", "대화방을 생성할 수 없습니다.", false); }
    }

    private void CloseInvite_Click(object sender, RoutedEventArgs e) => chatInviteModal.Visibility = Visibility.Collapsed;
    
    private void InviteRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is EmployeeModel emp) {
            emp.IsSelected = !emp.IsSelected;
            // No need to re-assign ItemsSource because EmployeeModel implements INotifyPropertyChanged
        }
    }

    private async void CreateChatFromInvite_Click(object sender, RoutedEventArgs e)
    {
        // 직원 탭(_allEmployees)과 조직 탭(_inviteOrgTree) 선택은 서로 다른 인스턴스일 수 있으므로
        // 양쪽 선택을 UserId 기준 합집합으로 모은다.
        var orgSelected = _inviteOrgTree.SelectMany(CollectInviteMembers);
        var recipients = _allEmployees.Where(emp => emp.IsSelected)
            .Concat(orgSelected.Where(emp => emp.IsSelected))
            .Where(emp => emp.UserId != _myUserId)
            .GroupBy(emp => emp.UserId)
            .Select(g => g.First())
            .ToList();

        if (recipients.Count == 0) { await ShowCustomModal("알림", "본인 외 대화 상대를 한 명 이상 선택하세요.", false); return; }
        chatInviteModal.Visibility = Visibility.Collapsed;
        ShowLoading("방 생성 중 (0/1)");
        
        long? roomId = null;
        if (recipients.Count == 1) 
        {
            // 1:1 chat - OpenDirectRoomAsync handles existence check and 1:1 logic
            roomId = await _apiService.OpenDirectRoomAsync(recipients[0].UserId);
        }
        else 
        {
            string title = string.Join(", ", recipients.Select(emp => emp.Name).Take(3));
            var recipientCount = recipients.Count;
            if (recipientCount > 3) title += $" 외 {recipientCount - 3}명";

            roomId = await _apiService.CreateRoomAsync(title, recipients.Select(emp => emp.UserId).ToList());
        }
        
        txtLoadingStatus.Text = "방 생성 중 (1/1)";
        HideLoading();
        if (roomId.HasValue) { RefreshPanelData(); OpenChatWindow(roomId.Value, null); }
        else { await ShowCustomModal("오류", "대화방을 생성할 수 없습니다.", false); }
    }

    private void TxtInviteSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = txtInviteSearch.Text.ToLower();
        if (string.IsNullOrWhiteSpace(query)) lstInviteMembers.ItemsSource = _allEmployees;
        else lstInviteMembers.ItemsSource = _allEmployees.Where(emp => 
            (emp.Name?.ToLower().Contains(query) ?? false) || (emp.TeamName?.ToLower().Contains(query) ?? false)).ToList();
    }

    private List<OrgNodeModel> BuildInviteOrgTree(IEnumerable<OrgNodeModel> nodes)
    {
        return nodes.Select(CloneInviteOrgNode).ToList();
    }

    private OrgNodeModel CloneInviteOrgNode(OrgNodeModel node)
    {
        return new OrgNodeModel
        {
            Name = node.Name,
            NodeType = node.NodeType,
            IsExpanded = false,
            Members = node.Members,
            Children = node.Children.Select(CloneInviteOrgNode).ToList()
        };
    }

    private IEnumerable<EmployeeModel> CollectInviteMembers(OrgNodeModel node)
    {
        foreach (var member in node.Members)
        {
            if (member.UserId != _myUserId)
            {
                yield return member;
            }
        }

        foreach (var child in node.Children)
        {
            foreach (var member in CollectInviteMembers(child))
            {
                yield return member;
            }
        }
    }

    private void InviteOrgNodeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not OrgNodeModel node)
        {
            return;
        }

        node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
        trvInviteOrg.Items.Refresh();
    }

    private void InviteOrgNodeSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not OrgNodeModel node)
        {
            return;
        }

        var members = CollectInviteMembers(node).DistinctBy(emp => emp.UserId).ToList();
        if (members.Count > 0)
        {
            bool selectAll = members.Any(emp => !emp.IsSelected);
            foreach (var member in members)
            {
                member.IsSelected = selectAll;
            }
        }

        e.Handled = true;
        trvInviteOrg.Items.Refresh();
        lstInviteMembers.Items.Refresh();
    }

    private void LstMessages_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (lstMessages.SelectedItem is MessageRoomModel room) OpenChatWindow(room.Id, room.Title);
    }

    private void OpenRoomContext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRoom(sender) is MessageRoomModel room) OpenChatWindow(room.Id, room.Title);
    }

    private async void RenameRoomContext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRoom(sender) is not MessageRoomModel room) return;
        var name = ShowTextInputDialog("채팅방 이름 변경", "내 화면에 표시할 채팅방 이름을 입력하세요.", room.CustomTitle ?? room.Title ?? "");
        if (name == null) return;
        var updated = await _apiService.UpdateRoomSettingsAsync(room.Id, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), room.Pinned, room.Muted, room.OpacityPercent);
        if (updated != null)
        {
            room.Apply(updated);
            RefreshPanelData();
        }
    }

    private async void TogglePinRoomContext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRoom(sender) is not MessageRoomModel room) return;
        var updated = await _apiService.UpdateRoomSettingsAsync(room.Id, room.CustomTitle, !room.Pinned, room.Muted, room.OpacityPercent);
        if (updated != null)
        {
            room.Apply(updated);
            RefreshPanelData();
        }
    }

    private async void ToggleMuteRoomContext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRoom(sender) is not MessageRoomModel room) return;
        var updated = await _apiService.UpdateRoomSettingsAsync(room.Id, room.CustomTitle, room.Pinned, !room.Muted, room.OpacityPercent);
        if (updated != null)
        {
            room.Apply(updated);
            RefreshPanelData();
        }
    }

    private async void LeaveRoomContext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRoom(sender) is not MessageRoomModel room) return;
        if (!await ShowCustomModal("채팅방 나가기", $"'{room.Title}' 채팅방을 나가시겠습니까?", true)) return;
        ShowLoading("채팅방 나가기 중 (0/1)");
        var ok = await _apiService.LeaveRoomAsync(room.Id);
        txtLoadingStatus.Text = "채팅방 나가기 중 (1/1)";
        HideLoading();
        if (ok)
        {
            if (_chatWindows.TryGetValue(room.Id, out var win)) win.Close();
            RemoveRoomFromMessageList(room.Id);
        }
        else await ShowCustomModal("오류", "채팅방을 나갈 수 없습니다.", false);
    }

    private static MessageRoomModel? GetContextRoom(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is MessageRoomModel direct) return direct;
            if (fe.Parent is ContextMenu menu && menu.PlacementTarget is FrameworkElement target)
                return target.DataContext as MessageRoomModel;
        }
        return null;
    }

    private async void OpenChatWindow(long roomId, string? title)
    {
        // 채팅방을 여는 순간 해당 방의 메시지 알림을 읽음 처리한다.
        MarkRoomReadInMessageList(roomId);
        MarkRoomNotificationsReadLocally(roomId);
        _ = _apiService.MarkRoomNotificationsReadAsync(roomId);

        if (_chatWindows.TryGetValue(roomId, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show();
            existing.Topmost = true;
            existing.Activate();
            existing.Topmost = false; // Briefly set Topmost to force it above other windows
            return;
        }
        var roomSettings = (lstMessages.ItemsSource as IEnumerable<MessageRoomModel>)?.FirstOrDefault(r => r.Id == roomId);
        var window = new ChatRoomWindow(_apiService, roomId, title, _myUserId, BuildInviteOrgTree(_fullOrgTree), roomSettings);
        // [1.0.85] 저장된 위치/크기가 있고 화면 안에 들어가면 그걸 우선. 아니면 메인창 왼쪽 도킹.
        ApplySavedChatRect(window, roomId);
        window.LocationChanged += (_, _) => SaveChatRect(window, roomId);
        window.SizeChanged += (_, _) => SaveChatRect(window, roomId);
        window.RoomLeft += leftRoomId => Dispatcher.Invoke(() => RemoveRoomFromMessageList(leftRoomId));
        window.SaveAsWorkMemoRequested += (body, paths) => Dispatcher.Invoke(() => OpenWorkMemoCompose(body, paths));
        window.RoomSettingsChanged += updated => Dispatcher.Invoke(() =>
        {
            if ((lstMessages.ItemsSource as IEnumerable<MessageRoomModel>)?.FirstOrDefault(r => r.Id == updated.Id) is MessageRoomModel room)
            {
                room.Apply(updated);
                lstMessages.Items.Refresh();
            }
        });
        window.OpenDirectChatRequested += uid => Dispatcher.Invoke(() => OpenDirectChat(uid, null));
        window.Closed += (_, _) => _chatWindows.Remove(roomId);
        _chatWindows[roomId] = window;
        
        window.Show();
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
    }

    /// <summary>해당 방의 채팅창이 활성 상태이면 메시지 알림 표시를 막는다.</summary>
    private bool IsChatRoomFocused(long roomId)
    {
        if (!_chatWindows.TryGetValue(roomId, out var w) || w == null) return false;
        // 최소화 상태이거나 다른 창에 포커스가 있으면 알림을 표시한다.
        return w.IsActive && w.IsVisible && w.WindowState != WindowState.Minimized;
    }

    private bool IsChatRoomOpen(long roomId)
    {
        if (!_chatWindows.TryGetValue(roomId, out var w) || w == null) return false;
        return w.IsVisible;
    }

}
