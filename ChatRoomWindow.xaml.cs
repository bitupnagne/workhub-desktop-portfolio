using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Portfolio.OfficeSystem;

public partial class ChatRoomWindow : Window
{
    private readonly WorkSystemApiService _apiService;
    private readonly AppSettingsService _settingsService = new();
    private readonly long _roomId;
    private readonly long _myUserId;
    private readonly DispatcherTimer _refreshTimer = new();
    // [PERF] 빠른 연속 전송/이벤트로 RefreshChatRoom이 폭주하는 것을 방지: 짧은 시간 안의 호출을 1회로 합친다.
    private readonly DispatcherTimer _refreshDebouncer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _refreshing;
    // [PERF] 증분 갱신 — UpdateChatList가 ItemsSource를 통째 교체하지 않고 id 기반 diff로 최소 변경만 적용.
    private readonly System.Collections.ObjectModel.ObservableCollection<MessageModel> _displayItems = new();
    private List<EmployeeModel> _inviteCandidates = new();
    private List<OrgNodeModel> _sourceOrgTree = new();
    private List<OrgNodeModel> _inviteOrgTree = new();
    private List<EmployeeModel> _currentMembers = new();
    private ParticipantsWindow? _participantsWindow;
    private List<MessageModel> _serverMessages = new();
    private List<MessageModel> _pendingMessages = new();
    private MessageRoomModel _roomSettings = new();
    private bool _applyingOpacity;
    private readonly DispatcherTimer _opacitySaveTimer = new();
    private List<MessageModel> _chatSearchMatches = new();
    private int _chatSearchIndex;
    private string _chatSearchQuery = "";
    private System.Windows.Controls.ScrollViewer? _chatScroll;
    private bool _chatPinnedToBottom = true;
    // 페이지네이션(위로 스크롤 시 과거 로드)
    private const int ChatPageSize = 80;
    private List<MessageModel> _olderMessages = new();
    private bool _chatHasMore = false;
    private bool _loadingOlder = false;
    private bool _anchorTop = false; // 과거 로드 직후: 하단 스냅 끄고 위치 보존
    private readonly List<PendingChatAttachment> _pendingAttachments = new();
    private readonly Dictionary<string, System.Threading.CancellationTokenSource> _uploadCts = new();
    private MemberDetailWindow? _memberDetailWindow;
    public event Action<long>? RoomLeft;
    public event Action<MessageRoomModel>? RoomSettingsChanged;
    // 직원 상세창의 1:1 대화 요청 → MainWindow 가 방을 연다. (userId)
    public event Action<long>? OpenDirectChatRequested;
    // 채팅 메시지/첨부를 업무전달로 변환 요청 → MainWindow 가 작성 창을 띄운다.
    public event Action<string?, IReadOnlyList<string>>? SaveAsWorkMemoRequested;

    public ChatRoomWindow(WorkSystemApiService apiService, long roomId, string? title, long myUserId, List<OrgNodeModel>? orgTree = null, MessageRoomModel? roomSettings = null)
    {
        InitializeComponent();
        _apiService = apiService;
        _roomId = roomId;
        _myUserId = myUserId;
        _sourceOrgTree = orgTree ?? new List<OrgNodeModel>();
        _roomSettings = roomSettings ?? new MessageRoomModel { Id = roomId, Title = title, OpacityPercent = 100 };

        if (!string.IsNullOrEmpty(title)) txtChatTitle.Text = title;
        lstChatMessages.ItemsSource = _displayItems;
        ApplyRoomSettings(_roomSettings, true);
        ApplyMessageFontSize(_settingsService.Load().MessageFontSizeMode);

        Loaded += async (_, _) => {
            await RefreshChatRoom();
        };
        Closing += (s, e) => {
            if (_uploadCts.Any())
            {
                var result = MessageBox.Show(this, "현재 파일 전송이 진행 중입니다.\n창을 닫으면 전송이 취소됩니다. 그래도 닫으시겠습니까?", "알림", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                foreach (var cts in _uploadCts.Values) { try { cts.Cancel(); } catch { } }
            }
        };
        Closed += (_, _) => { _refreshTimer.Stop(); _refreshDebouncer.Stop(); _participantsWindow?.Close(); _memberDetailWindow?.Close(); ThemeService.ThemeChanged -= OnThemeChanged; };
        ThemeService.ThemeChanged += OnThemeChanged;
        PreviewKeyDown += (s, e) => {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.V && Clipboard.ContainsImage())
            {
                e.Handled = true;
                AddClipboardImagePreview();
                return;
            }
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };

        // [S-1] 폴링 주기 — WebSocket 끊긴 fallback 용도. WebSocket 연결되어 있으면 정지.
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += async (_, _) => {
            if (_apiService.IsWebSocketConnected) return; // WebSocket 동작 시 폴링 skip
            await RefreshChatRoom();
        };
        _refreshTimer.Start();

        // [PERF] 디바운서 — 마지막 호출 후 150ms 정지하면 한 번만 실행
        _refreshDebouncer.Tick += async (_, _) =>
        {
            _refreshDebouncer.Stop();
            if (_refreshing) { _refreshDebouncer.Start(); return; }
            _refreshing = true;
            try { await RefreshChatRoom(); }
            finally { _refreshing = false; }
        };

        // [S-1] MESSAGE_CHANGED 이벤트 — 해당 방이면 즉시 갱신
        _messageChangedHandler = rid => {
            if (rid == _roomId) Dispatcher.Invoke(QueueRefreshChatRoom);
        };
        _apiService.OnMessageChanged += _messageChangedHandler;
        Closed += (_, _) => { _apiService.OnMessageChanged -= _messageChangedHandler; };

        _messageCreatedHandler = payload => {
            Dispatcher.Invoke(() => TryAppendCreatedMessage(payload));
        };
        _apiService.OnMessageCreated += _messageCreatedHandler;
        Closed += (_, _) => { _apiService.OnMessageCreated -= _messageCreatedHandler; };

        _connectedHandler = () => Dispatcher.Invoke(QueueRefreshChatRoom);
        _apiService.OnWebSocketConnected += _connectedHandler;
        Closed += (_, _) => { _apiService.OnWebSocketConnected -= _connectedHandler; };

        _opacitySaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _opacitySaveTimer.Tick += async (_, _) =>
        {
            _opacitySaveTimer.Stop();
            await SaveRoomSettingsAsync();
        };
    }

    private Action<long>? _messageChangedHandler;
    private Action<JsonElement>? _messageCreatedHandler;
    private Action? _connectedHandler;

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { this.DragMove(); } catch { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void CancelUpload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageModel message && !string.IsNullOrEmpty(message.TempKey))
        {
            if (_uploadCts.TryGetValue(message.TempKey, out var cts))
            {
                cts.Cancel();
            }
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        btnSettings.ContextMenu.PlacementTarget = btnSettings;
        btnSettings.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btnSettings.ContextMenu.IsOpen = true;
    }

    private void MessageFontSmall_Click(object sender, RoutedEventArgs e) => SaveMessageFontSize("Small");
    private void MessageFontMedium_Click(object sender, RoutedEventArgs e) => SaveMessageFontSize("Medium");
    private void MessageFontLarge_Click(object sender, RoutedEventArgs e) => SaveMessageFontSize("Large");

    private void SaveMessageFontSize(string mode)
    {
        var settings = _settingsService.Load();
        settings.MessageFontSizeMode = mode;
        _settingsService.Save(settings);
        ApplyMessageFontSize(mode);
        btnSettings.ContextMenu.IsOpen = false;
    }

    private void ApplyMessageFontSize(string? mode)
    {
        mode = string.IsNullOrWhiteSpace(mode) ? "Small" : mode;
        var size = mode switch
        {
            "Medium" => 16d,
            "Large" => 18d,
            _ => 14d
        };
        Resources["MessageBodyFontSize"] = size;
        Resources["MessageBodyLineHeight"] = size switch
        {
            >= 18d => 27d,
            >= 16d => 24d,
            _ => 21d
        };
        if (mnuMessageFontSmall != null) mnuMessageFontSmall.IsChecked = mode == "Small";
        if (mnuMessageFontMedium != null) mnuMessageFontMedium.IsChecked = mode == "Medium";
        if (mnuMessageFontLarge != null) mnuMessageFontLarge.IsChecked = mode == "Large";
        lstChatMessages?.Items.Refresh();
    }

    // 펜딩(낙관적) 메시지 정합. 일반 텍스트는 동일 본문이 서버에 도착하면 제거.
    // 업로드 placeholder는 '내 첨부 메시지 수'가 시작 시점보다 늘면(=서버 도착) 제거 → 로딩 잔상 방지.
    private static List<MessageModel> ReconcilePending(List<MessageModel> pending, List<MessageModel> server)
    {
        int mineAttachMsgs = server.Count(m => m.IsMine && m.Attachments.Count > 0);
        return pending.Where(p => p.IsUploading
            ? mineAttachMsgs <= p.UploadBaselineCount
            : !server.Any(m => m.IsMine && string.Equals(m.Body, p.Body, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// 디바운스된 새로고침 요청. 연속 호출은 150ms 안에서 1회로 합쳐진다.
    /// 빠른 연속 전송·WebSocket 이벤트 폭주에 의한 UI 렉 방지용.
    /// </summary>
    private void QueueRefreshChatRoom()
    {
        _refreshDebouncer.Stop();
        _refreshDebouncer.Start();
    }

    // 테마 변경 시 ListBox의 컨테이너가 캐시된 색을 못 따라오는 케이스 방어:
    // ItemsSource를 한 박자 후 다시 바인딩해 컨테이너 재realize → 새 DynamicResource 평가.
    private void OnThemeChanged(ThemeMode mode)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var src = lstChatMessages.ItemsSource;
                lstChatMessages.ItemsSource = null;
                lstChatMessages.ItemsSource = src;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch { }
    }

    private async Task RefreshChatRoom()
    {
        var data = await _apiService.GetMessageStreamAsync(_roomId, ChatPageSize);
        if (!data.HasValue) return;

        // 과거 미로드 상태에서만 payload.hasMore를 신뢰. 과거 로드 후엔 /before 응답이 기준이라 유지.
        if (_olderMessages.Count == 0)
        {
            _chatHasMore = data.Value.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True;
        }

        try
        {
            long myId = data.Value.TryGetProperty("currentUserId", out var idProp) ? idProp.GetInt64() : _myUserId;
            
            if (data.Value.TryGetProperty("room", out var roomProp)) {
                var updatedRoom = MessageRoomModel.FromJson(roomProp);
                _roomSettings.Apply(updatedRoom);
                ApplyRoomSettings(_roomSettings, false);
                RoomSettingsChanged?.Invoke(_roomSettings);
            }

            if (data.Value.TryGetProperty("messages", out var msgsProp))
            {
                var incomingMessages = msgsProp.EnumerateArray()
                    .Select(m => {
                        var senderUserId = m.TryGetProperty("senderUserId", out var suid) && suid.ValueKind != JsonValueKind.Null ? suid.GetInt64() : (long?)null;
                        var createdAtValue = ParseMessageCreatedAt(m);
                        return new MessageModel
                        {
                            Id = m.GetProperty("id").GetInt64(),
                            SenderUserId = senderUserId,
                            SenderName = ExtractName(m),
                            SenderProfileImageFilename = m.TryGetProperty("senderProfileImageFilename", out var pf) && pf.ValueKind != JsonValueKind.Null ? pf.GetString() : null,
                            Body = m.TryGetProperty("body", out var b) ? b.GetString() : "",
                            Attachments = MapAttachments(m),
                            CreatedAt = createdAtValue,
                            CreatedAtLabel = createdAtValue?.ToString("HH:mm") ?? "",
                            IsMine = senderUserId == myId,
                            IsSystem = senderUserId == null
                        };
                    }).ToList();

                bool messagesChanged = !incomingMessages.SequenceEqual(_serverMessages, new MessageModelComparer());
                if (messagesChanged)
                {
                    _serverMessages = incomingMessages;
                    _pendingMessages = ReconcilePending(_pendingMessages, incomingMessages);
                    UpdateChatList();
                }
            }

            if (data.Value.TryGetProperty("members", out var members))
            {
                var incoming = members.EnumerateArray().Select(MapEmployee).ToList();
                bool structureChanged = ApplyMembersDelta(_currentMembers, incoming);
                var names = _currentMembers.Select(m => m.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                var displayNames = names.Take(4).ToList();
                string suffix = names.Count > 4 ? $" 외 {names.Count - 4}명" : "";
                txtParticipants.Text = names.Count == 0 ? "참여자 없음" : string.Join(", ", displayNames) + suffix;
                if (lstMembers.ItemsSource == null || structureChanged)
                {
                    lstMembers.ItemsSource = null;
                    lstMembers.ItemsSource = _currentMembers;
                    _participantsWindow?.UpdateMembers(_currentMembers);
                }
            }

            if (data.Value.TryGetProperty("inviteCandidates", out var candidates))
            {
                var newCandidates = candidates.EnumerateArray().Select(MapEmployee).ToList();
                
                // Only update the list entirely if the modal is NOT open to prevent destroying selection state
                if (inviteModal.Visibility != Visibility.Visible)
                {
                    _inviteCandidates = newCandidates;
                }
                else
                {
                    // If open, just update statuses (if needed) but keep the existing list to maintain selections
                    foreach (var newEmp in newCandidates)
                    {
                        var existing = _inviteCandidates.FirstOrDefault(e => e.UserId == newEmp.UserId);
                        if (existing != null)
                        {
                            existing.Online = newEmp.Online;
                            existing.StatusLabel = newEmp.StatusLabel;
                            existing.StatusClass = newEmp.StatusClass;
                        }
                    }
                }
            }
        }
        catch { }
    }

    private string? ExtractName(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        // Common name fields
        string[] fields = { "senderName", "name", "realName", "userName", "nickName" };
        foreach (var f in fields) {
            if (e.TryGetProperty(f, out var val) && !string.IsNullOrEmpty(val.GetString())) return val.GetString();
        }

        // Check for common nested objects
        string[] objects = { "user", "employee", "sender" };
        foreach (var obj in objects) {
            if (e.TryGetProperty(obj, out var nested)) {
                var name = ExtractName(nested);
                if (name != null) return name;
            }
        }
        return null;
    }

    private List<MessageAttachmentModel> MapAttachments(JsonElement message)
    {
        if (!message.TryGetProperty("attachments", out var attachments)
            || attachments.ValueKind != JsonValueKind.Array)
        {
            return new List<MessageAttachmentModel>();
        }
        return attachments.EnumerateArray().Select(a => new MessageAttachmentModel
        {
            Id = a.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
            OriginalFilename = a.TryGetProperty("originalFilename", out var name) ? name.GetString() : "",
            ContentType = a.TryGetProperty("contentType", out var ct) ? ct.GetString() : "",
            FileSize = a.TryGetProperty("fileSize", out var size) ? size.GetInt64() : 0,
            AttachmentType = a.TryGetProperty("attachmentType", out var type) ? type.GetString() : "",
            ExpiresAt = a.TryGetProperty("expiresAt", out var exp) ? exp.ToString() : ""
        }).Where(a => a.Id > 0).ToList();
    }

    private DateTime? ParseMessageCreatedAt(JsonElement message)
    {
        if (!message.TryGetProperty("createdAt", out var ca)) return null;
        try
        {
            if (ca.ValueKind == JsonValueKind.String && DateTime.TryParse(ca.GetString(), out var parsed)) return parsed;
            if (ca.ValueKind == JsonValueKind.Array)
            {
                var values = ca.EnumerateArray().Select(v => v.GetInt32()).ToArray();
                if (values.Length >= 5)
                {
                    var second = values.Length >= 6 ? values[5] : 0;
                    return new DateTime(values[0], values[1], values[2], values[3], values[4], second);
                }
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private void TryAppendCreatedMessage(JsonElement payload)
    {
        try
        {
            if (!payload.TryGetProperty("roomId", out var roomProp) || roomProp.GetInt64() != _roomId) return;
            if (!payload.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;

            var id = m.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
            if (id <= 0 || _serverMessages.Any(existing => existing.Id == id)) return;

            long? senderUserId = null;
            if (m.TryGetProperty("senderUserId", out var senderProp))
            {
                if (senderProp.ValueKind == JsonValueKind.Number) senderUserId = senderProp.GetInt64();
                else if (senderProp.ValueKind == JsonValueKind.String && long.TryParse(senderProp.GetString(), out var parsed)) senderUserId = parsed;
            }

            var createdAtValue = ParseMessageCreatedAt(m) ?? DateTime.Now;
            string createdAtLabel = createdAtValue.ToString("HH:mm");

            _serverMessages.Add(new MessageModel
            {
                Id = id,
                SenderUserId = senderUserId,
                SenderName = ExtractName(m),
                SenderProfileImageFilename = m.TryGetProperty("senderProfileImageFilename", out var pf) && pf.ValueKind == JsonValueKind.String ? pf.GetString() : null,
                Body = m.TryGetProperty("body", out var b) ? b.GetString() : "",
                Attachments = MapAttachments(m),
                CreatedAt = createdAtValue,
                CreatedAtLabel = createdAtLabel,
                IsMine = senderUserId == _myUserId,
                IsSystem = senderUserId == null
            });

            _pendingMessages = ReconcilePending(_pendingMessages, _serverMessages);
            UpdateChatList();
        }
        catch
        {
            _ = RefreshChatRoom();
        }
    }

    private EmployeeModel MapEmployee(JsonElement m)
    {
        long id = 0;
        if (m.TryGetProperty("id", out var idProp)) id = idProp.GetInt64();
        else if (m.TryGetProperty("userId", out var uidProp)) id = uidProp.GetInt64();

        return new EmployeeModel
        {
            UserId = id,
            Name = ExtractName(m) ?? "알 수 없음",
            JobTitle = m.TryGetProperty("jobTitle", out var job) ? job.GetString() : null,
            TeamName = m.TryGetProperty("teamName", out var team) ? team.GetString() : null,
            Email = m.TryGetProperty("email", out var e) ? e.GetString() : null,
            MobileNumber = m.TryGetProperty("mobileNumber", out var mob) ? mob.GetString() : null,
            ExtensionNumber = m.TryGetProperty("extensionNumber", out var ext) ? ext.GetString() : null,
            ProfileImageFilename = m.TryGetProperty("profileImageFilename", out var img) ? img.GetString() : null,
            Online = m.TryGetProperty("online", out var on) ? on.GetBoolean() : false,
            StatusLabel = m.TryGetProperty("statusLabel", out var sl) ? sl.GetString() : null,
            StatusClass = m.TryGetProperty("statusClass", out var sc) ? sc.GetString() : null
        };
    }

    private async void SendMessage_Click(object sender, RoutedEventArgs e)
    {
        string body = txtChatInput.Text.Trim();
        if (string.IsNullOrEmpty(body) && !_pendingAttachments.Any()) return;
        txtChatInput.Text = "";

        if (_pendingAttachments.Any())
        {
            var paths = _pendingAttachments.Select(file => file.Path).ToList();
            long totalBytes = 0;
            foreach (var p in paths) { try { totalBytes += new FileInfo(p).Length; } catch { } }
            string sizeLabel = FormatBytes(totalBytes);
            ClearPendingAttachments();

            // Optimistic: 업로드 진행 메시지(용량 + 진행바)를 즉시 표시. 입력창은 그대로 사용 가능.
            var tempKey = System.Guid.NewGuid().ToString();
            var uploading = new MessageModel
            {
                Id = -DateTime.Now.Ticks,
                TempKey = tempKey,
                SenderUserId = _myUserId,
                SenderName = "나",
                Body = body,
                CreatedAtLabel = DateTime.Now.ToString("HH:mm"),
                CreatedAt = DateTime.Now,
                IsMine = true,
                IsSystem = false,
                IsUploading = true,
                UploadPercent = 0,
                UploadInfo = $"파일 {paths.Count}개 · {sizeLabel} · 0%",
                UploadBaselineCount = _serverMessages.Count(m => m.IsMine && m.Attachments.Count > 0)
            };
            _pendingMessages.Add(uploading);
            UpdateChatList();

            var cts = new System.Threading.CancellationTokenSource();
            _uploadCts[tempKey] = cts;

            try
            {
                var progress = new Progress<double>(pct =>
                {
                    uploading.UploadPercent = pct;
                    uploading.UploadInfo = $"파일 {paths.Count}개 · {sizeLabel} · {(int)pct}%";
                });
                var okWithFiles = await _apiService.SendMessageAttachmentsAsync(_roomId, body, paths, progress, cts.Token);
                _pendingMessages.Remove(uploading);
                _uploadCts.Remove(tempKey);

                if (okWithFiles)
                {
                    QueueRefreshChatRoom(); // 디바운스 — 연속 전송 시 폭주 방지
                }
                else if (!cts.IsCancellationRequested)
                {
                    UpdateChatList();
                    MessageBox.Show(this, "첨부파일 업로드에 실패했습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    UpdateChatList();
                }
            }
            catch (OperationCanceledException)
            {
                _pendingMessages.Remove(uploading);
                _uploadCts.Remove(tempKey);
                UpdateChatList();
            }
            finally
            {
                cts.Dispose();
            }
            return;
        }

        // Optimistic: 즉시 본인 화면에 표시 (스피너 + 로컬 시간)
        var pending = new MessageModel
        {
            Id = -DateTime.Now.Ticks, // 임시
            TempKey = System.Guid.NewGuid().ToString(),
            SenderUserId = _myUserId,
            SenderName = "나",
            Body = body,
            CreatedAtLabel = DateTime.Now.ToString("HH:mm"),
            CreatedAt = DateTime.Now,
            IsMine = true,
            IsSystem = false,
            IsPending = true
        };
        _pendingMessages.Add(pending);
        UpdateChatList();

        var ok = await _apiService.SendMessageAsync(_roomId, body);
        if (ok)
        {
            // WebSocket 사용 시 MESSAGE_CHANGED 이벤트가 RefreshChatRoom을 트리거 → 별도 호출 불필요
            // HTTP fallback 시: 디바운스로 합쳐서 호출 (빠른 연속 전송 대비)
            if (!_apiService.IsWebSocketConnected) QueueRefreshChatRoom();
        }
        else
        {
            pending.IsPending = false;
            pending.IsFailed = true;
        }
    }

    private void AttachMenu_Click(object sender, RoutedEventArgs e)
    {
        btnAttach.ContextMenu.PlacementTarget = btnAttach;
        btnAttach.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        btnAttach.ContextMenu.IsOpen = true;
    }

    private void AttachImage_Click(object sender, RoutedEventArgs e)
    {
        SelectPendingFiles("사진 선택", "이미지|*.jpg;*.jpeg;*.png;*.gif;*.webp|모든 파일|*.*");
    }

    private void AttachVideo_Click(object sender, RoutedEventArgs e)
    {
        SelectPendingFiles("동영상 선택", "동영상|*.mp4;*.mov;*.webm|모든 파일|*.*");
    }

    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        SelectPendingFiles("첨부파일 선택", "모든 파일|*.*");
    }

    private void SelectPendingFiles(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = title,
            Filter = filter
        };
        if (dialog.ShowDialog(this) != true) return;
        AddPendingFiles(dialog.FileNames);
    }

    private void AddPendingFiles(IEnumerable<string> paths)
    {
        var incoming = paths.Where(File.Exists).ToList();
        if (!incoming.Any()) return;
        long total = _pendingAttachments.Sum(file => new FileInfo(file.Path).Length)
            + incoming.Sum(path => new FileInfo(path).Length);
        if (total > 100L * 1024L * 1024L)
        {
            MessageBox.Show(this, "첨부파일은 한 번에 총 100MB까지 업로드할 수 있습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _pendingAttachments.AddRange(incoming.Select(PendingChatAttachment.FromPath));
        RenderPendingAttachments();
    }

    private void AddClipboardImagePreview()
    {
        try
        {
            var image = Clipboard.GetImage();
            if (image == null) return;

            var dir = Path.Combine(Path.GetTempPath(), "Portfolio.OfficeSystem", "clipboard");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
            AddPendingFiles(new[] { path });
        }
        catch
        {
            MessageBox.Show(this, "클립보드 이미지를 처리할 수 없습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenderPendingAttachments()
    {
        pendingAttachmentsPanel.ItemsSource = null;
        pendingAttachmentsPanel.ItemsSource = _pendingAttachments;
        pendingAttachmentsPanel.Visibility = _pendingAttachments.Any() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearPendingAttachments()
    {
        _pendingAttachments.Clear();
        RenderPendingAttachments();
    }

    private void RemovePendingAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PendingChatAttachment attachment)
        {
            _pendingAttachments.Remove(attachment);
            RenderPendingAttachments();
        }
    }

    // 채팅 말풍선/첨부 컨텍스트 메뉴: "업무전달로 저장"
    private async void SaveAsWorkMemo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        string? body = null;
        var attachmentPaths = new List<string>();
        if (fe.DataContext is MessageModel msg)
        {
            body = string.IsNullOrWhiteSpace(msg.Body) ? null : msg.Body;
            // 첨부가 있으면 임시 디렉터리로 다운로드
            if (msg.Attachments != null)
            {
                foreach (var att in msg.Attachments)
                {
                    var p = await _apiService.DownloadMessageAttachmentAsync(att);
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) attachmentPaths.Add(p!);
                }
            }
        }
        else if (fe.DataContext is MessageAttachmentModel att)
        {
            var p = await _apiService.DownloadMessageAttachmentAsync(att);
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) attachmentPaths.Add(p!);
        }
        SaveAsWorkMemoRequested?.Invoke(body, attachmentPaths);
        e.Handled = true;
    }

    private async void Attachment_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageAttachmentModel attachment) return;
        try
        {
            if (attachment.IsImage)
            {
                var ownerMessage = _serverMessages.FirstOrDefault(message => message.Attachments.Any(file => file.Id == attachment.Id));
                var images = (ownerMessage?.Attachments ?? new List<MessageAttachmentModel>())
                    .Where(file => file.IsImage)
                    .Select(file => file.DownloadUrl)
                    .Distinct()
                    .ToList();
                var index = Math.Max(0, images.IndexOf(attachment.DownloadUrl));
                ImagePreviewWindow.Show(this, images.Count > 0 ? images : new[] { attachment.DownloadUrl }, index);
                return;
            }
            var path = await _apiService.DownloadMessageAttachmentAsync(attachment);
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, "첨부파일 다운로드에 실패했습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>
    /// 기존 _currentMembers 객체 reference를 유지한 채 setter만 호출해 상태(online/statusClass)를 갱신한다.
    /// 멤버 추가/제거가 있는 경우만 true 반환 — 그때만 ItemsSource를 재바인딩해야 한다.
    /// </summary>
    private bool ApplyMembersDelta(List<EmployeeModel> existing, List<EmployeeModel> incoming)
    {
        var existingById = existing.ToDictionary(m => m.UserId);
        var incomingIds = incoming.Select(m => m.UserId).ToHashSet();
        bool structureChanged = false;

        // 제거된 멤버
        for (int i = existing.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(existing[i].UserId))
            {
                existing.RemoveAt(i);
                structureChanged = true;
            }
        }
        // 신규 추가
        foreach (var inc in incoming)
        {
            if (!existingById.ContainsKey(inc.UserId))
            {
                existing.Add(inc);
                structureChanged = true;
            }
            else
            {
                // 기존 객체에 status/online만 setter로 갱신 → INotifyPropertyChanged로 점만 다시 칠해짐
                var ex = existingById[inc.UserId];
                ex.Online = inc.Online;
                ex.StatusLabel = inc.StatusLabel;
                ex.StatusClass = inc.StatusClass;
                ex.ProfileImageFilename = inc.ProfileImageFilename;
            }
        }
        return structureChanged;
    }

    // [PERF] ObservableCollection에 id 기준 prefix/suffix diff를 적용 — 같은 항목은 제자리에 두고 변경분만 Insert/Remove.
    // ItemsSource 통째 교체로 인한 컨테이너 재생성·레이아웃 비용을 최소화한다.
    private static void ApplyDiff(System.Collections.ObjectModel.ObservableCollection<MessageModel> current, List<MessageModel> desired)
    {
        int prefix = 0;
        int maxPrefix = Math.Min(current.Count, desired.Count);
        while (prefix < maxPrefix && current[prefix].Id == desired[prefix].Id) prefix++;
        int oldEnd = current.Count;
        int newEnd = desired.Count;
        while (oldEnd > prefix && newEnd > prefix && current[oldEnd - 1].Id == desired[newEnd - 1].Id)
        {
            oldEnd--; newEnd--;
        }
        for (int i = oldEnd - 1; i >= prefix; i--) current.RemoveAt(i);
        for (int i = prefix; i < newEnd; i++) current.Insert(i, desired[i]);
    }

    private void UpdateChatList()
    {
        // 1. 과거(_olderMessages) + 서버(_serverMessages) — id 기준 dedup. 펜딩(업로드 제외) 합쳐 시간 정렬.
        var serverById = new Dictionary<long, MessageModel>();
        foreach (var m in _olderMessages) if (m.Id > 0) serverById[m.Id] = m;
        foreach (var m in _serverMessages) if (m.Id > 0) serverById[m.Id] = m;
        var normalPending = _pendingMessages.Where(p => !p.IsUploading).ToList();
        var allNormal = serverById.Values.Concat(normalPending)
            .OrderBy(m => m.CreatedAt ?? DateTime.MinValue)
            .ToList();

        var combined = new List<MessageModel>(allNormal.Count + _pendingMessages.Count + 8);
        string? lastDate = null;
        foreach (var message in allNormal)
        {
            var key = message.CreatedAt?.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(key) && key != lastDate)
            {
                combined.Add(MessageModel.DateSeparator(message.CreatedAt!.Value));
                lastDate = key;
            }
            combined.Add(message);
        }

        // 2. 업로드 중인 메시지는 항상 최하단에 배치
        var uploadingItems = _pendingMessages.Where(p => p.IsUploading).OrderBy(m => m.CreatedAt).ToList();
        combined.AddRange(uploadingItems);

        // [PERF] id 기반 diff — prefix/suffix 일치 구간 유지, 중간 구간만 Remove/Insert
        ApplyDiff(_displayItems, combined);
        ApplyChatSearch(false);
        if (!_anchorTop && string.IsNullOrWhiteSpace(txtChatSearch?.Text) && lstChatMessages.Items.Count > 0)
        {
            // 새 메시지/첨부 추가 시 항상 최하단으로. 이미지가 늦게 로드돼 높이가 커져도
            // ChatMessages_ScrollChanged 가 하단 고정을 유지한다.
            // (과거 로드 직후엔 LoadOlderAsync 가 직접 위치 복원하므로 여기는 건너뛴다.)
            _chatPinnedToBottom = true;
            var last = lstChatMessages.Items[lstChatMessages.Items.Count - 1];
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_chatScroll != null) _chatScroll.ScrollToBottom();
                else lstChatMessages.ScrollIntoView(last);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private async void ChatMessages_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.ScrollViewer sv) return;
        _chatScroll = sv;
        if (e.ExtentHeightChange == 0)
        {
            // 사용자가 스크롤한 경우: 하단 근처면 고정 유지, 아니면 해제
            _chatPinnedToBottom = sv.VerticalOffset >= sv.ScrollableHeight - 8;
            // 상단 근처로 올리면 과거 메시지 로드
            if (sv.VerticalOffset < 60 && _chatHasMore && !_loadingOlder && !_anchorTop)
            {
                await LoadOlderAsync();
            }
        }
        else if (_anchorTop)
        {
            // LoadOlderAsync 가 직접 위치 복원 — 여기서 하단 스냅 금지
        }
        else if (_chatPinnedToBottom)
        {
            // 콘텐츠가 커짐(새 메시지/이미지 로드 등) + 하단 고정 상태 → 계속 최하단 유지
            sv.ScrollToVerticalOffset(sv.ScrollableHeight);
        }
    }

    private async Task LoadOlderAsync()
    {
        if (_loadingOlder || !_chatHasMore || _chatScroll == null) return;
        // 현재 로드된 가장 오래된 메시지 id
        long oldestId = 0;
        if (_olderMessages.Count > 0) oldestId = _olderMessages[0].Id;
        else if (_serverMessages.Count > 0) oldestId = _serverMessages[0].Id;
        if (oldestId <= 0) return;

        _loadingOlder = true;
        try
        {
            var page = await _apiService.GetMessagesBeforeAsync(_roomId, oldestId, ChatPageSize);
            if (page == null) return;

            var older = new List<MessageModel>();
            if (page.Value.TryGetProperty("messages", out var msgs))
            {
                long myId = _myUserId;
                foreach (var m in msgs.EnumerateArray())
                {
                    var senderUserId = m.TryGetProperty("senderUserId", out var suid) && suid.ValueKind != JsonValueKind.Null ? suid.GetInt64() : (long?)null;
                    var createdAtValue = ParseMessageCreatedAt(m);
                    older.Add(new MessageModel
                    {
                        Id = m.GetProperty("id").GetInt64(),
                        SenderUserId = senderUserId,
                        SenderName = ExtractName(m),
                        SenderProfileImageFilename = m.TryGetProperty("senderProfileImageFilename", out var pf) && pf.ValueKind != JsonValueKind.Null ? pf.GetString() : null,
                        Body = m.TryGetProperty("body", out var b) ? b.GetString() : "",
                        Attachments = MapAttachments(m),
                        CreatedAt = createdAtValue,
                        CreatedAtLabel = createdAtValue?.ToString("HH:mm") ?? "",
                        IsMine = senderUserId == myId,
                        IsSystem = senderUserId == null
                    });
                }
            }
            if (older.Count > 0)
            {
                var existing = new HashSet<long>(_olderMessages.Select(m => m.Id));
                var add = older.Where(m => !existing.Contains(m.Id)).ToList();
                _olderMessages = add.Concat(_olderMessages).ToList();
            }
            _chatHasMore = page.Value.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True;

            // 위치 보존: ExtentHeight 변화량만큼 VerticalOffset 증가
            double prevExtent = _chatScroll.ExtentHeight;
            double prevOffset = _chatScroll.VerticalOffset;
            _anchorTop = true;
            UpdateChatList();
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_chatScroll != null)
                {
                    double delta = _chatScroll.ExtentHeight - prevExtent;
                    _chatScroll.ScrollToVerticalOffset(prevOffset + Math.Max(0, delta));
                }
                _anchorTop = false;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    private void ApplyChatSearch(bool jumpToFirst)
    {
        var query = _chatSearchQuery;
        if (string.IsNullOrEmpty(query))
        {
            _chatSearchMatches = new List<MessageModel>();
            _chatSearchIndex = 0;
            if (txtChatSearchCount != null) txtChatSearchCount.Text = "0/0";
            return;
        }

        var items = lstChatMessages.Items.OfType<MessageModel>().ToList();
        _chatSearchMatches = items
            .Where(m => !m.IsSystem && !string.IsNullOrWhiteSpace(m.Body)
                && m.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Reverse()
            .ToList();
        if (jumpToFirst) _chatSearchIndex = 0;
        if (_chatSearchMatches.Count == 0)
        {
            _chatSearchIndex = 0;
            txtChatSearchCount.Text = "0/0";
            return;
        }
        _chatSearchIndex = Math.Clamp(_chatSearchIndex, 0, _chatSearchMatches.Count - 1);
        txtChatSearchCount.Text = $"{_chatSearchIndex + 1}/{_chatSearchMatches.Count}";
        lstChatMessages.ScrollIntoView(_chatSearchMatches[_chatSearchIndex]);
    }

    private void MoveChatSearch(int delta)
    {
        if (_chatSearchMatches.Count == 0) return;
        _chatSearchIndex = (_chatSearchIndex + delta + _chatSearchMatches.Count) % _chatSearchMatches.Count;
        ApplyChatSearch(false);
    }

    // 입력만으로는 검색하지 않는다. 검색창을 비우면 하이라이트/카운트만 해제.
    private void TxtChatSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtChatSearch.Text))
        {
            _chatSearchQuery = "";
            _chatSearchIndex = 0;
            ApplyChatSearch(true);
        }
    }

    // 엔터를 쳐야 검색한다. 같은 검색어에서 엔터 반복 = 다음 결과로 이동.
    private void TxtChatSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var query = (txtChatSearch.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(query) && query == _chatSearchQuery && _chatSearchMatches.Count > 0)
        {
            MoveChatSearch(1);
        }
        else
        {
            _chatSearchQuery = query;
            _chatSearchIndex = 0;
            ApplyChatSearch(true);
        }
    }

    private void ChatSearchNext_Click(object sender, RoutedEventArgs e) => MoveChatSearch(1);
    private void ChatSearchPrev_Click(object sender, RoutedEventArgs e) => MoveChatSearch(-1);

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        searchPanel.Visibility = searchPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (searchPanel.Visibility == Visibility.Visible)
        {
            txtChatSearch.Focus();
            txtChatSearch.SelectAll();
        }
        else
        {
            txtChatSearch.Clear();
        }
    }

    private void ApplyRoomSettings(MessageRoomModel settings, bool updateSlider)
    {
        if (txtChatTitle != null && !string.IsNullOrWhiteSpace(settings.Title)) txtChatTitle.Text = settings.Title;
        if (roomAvatarHeader != null) roomAvatarHeader.Tiles = settings.AvatarTiles;
        if (icoPinned != null) icoPinned.Visibility = settings.Pinned ? Visibility.Visible : Visibility.Collapsed;
        if (icoMuted != null) icoMuted.Visibility = settings.Muted ? Visibility.Visible : Visibility.Collapsed;
        if (mnuTogglePin != null) mnuTogglePin.Header = settings.Pinned ? "채팅방 상단 고정 해제" : "채팅방 상단 고정";
        if (mnuToggleMute != null) mnuToggleMute.Header = settings.MuteActionText;

        _applyingOpacity = true;
        if (sldOpacity != null && (updateSlider || Math.Abs(sldOpacity.Value - settings.OpacityPercent) > 1))
        {
            sldOpacity.Value = settings.OpacityPercent;
        }
        if (lblOpacity != null) lblOpacity.Text = $"{settings.OpacityPercent}%";
        Opacity = Math.Max(0.05, settings.OpacityPercent / 100d);
        _applyingOpacity = false;
    }

    private void TxtChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendMessage_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void ShowInvite_Click(object sender, RoutedEventArgs e)
    {
        // [S-3] 초대 후보를 모달 열릴 때만 별도 fetch
        var fresh = await _apiService.GetInviteCandidatesAsync(_roomId);
        if (fresh != null) _inviteCandidates = fresh;
        if (_sourceOrgTree.Count == 0)
        {
            var panel = await _apiService.GetRightPanelDataAsync();
            if (panel.HasValue && panel.Value.TryGetProperty("orgTree", out var orgTree))
            {
                _sourceOrgTree = orgTree.EnumerateArray().Select(MapOrgNode).ToList();
            }
        }
        if (_inviteCandidates.Count == 0 && _sourceOrgTree.Count > 0)
        {
            var currentMemberIds = _currentMembers.Select(m => m.UserId).ToHashSet();
            _inviteCandidates = FlattenOrgMembers(_sourceOrgTree)
                .Where(emp => emp.UserId != _myUserId && !currentMemberIds.Contains(emp.UserId))
                .GroupBy(emp => emp.UserId)
                .Select(g => g.First())
                .ToList();
        }

        if (_activeInvitePicker != null && _activeInvitePicker.IsVisible) { _activeInvitePicker.Activate(); return; }
        var win = new MemberPickerWindow("대화 상대 초대", "초대", _inviteCandidates, _sourceOrgTree, _myUserId) { Owner = this };
        _activeInvitePicker = win;
        win.Confirmed += async ids =>
        {
            if (ids.Count > 0 && await _apiService.InviteMembersAsync(_roomId, ids))
                await RefreshChatRoom();
        };
        win.Closed += (_, _) => { if (ReferenceEquals(_activeInvitePicker, win)) _activeInvitePicker = null; };
        win.Show();
    }

    private MemberPickerWindow? _activeInvitePicker;

    private void CloseInvite_Click(object sender, RoutedEventArgs e)
    {
        inviteModal.Visibility = Visibility.Collapsed;
    }

    private void MemberRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is EmployeeModel emp)
        {
            ShowMemberDetail(emp);
            e.Handled = true;
        }
    }

    private void MessageAvatar_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MessageModel message && message.SenderUserId.HasValue)
        {
            var emp = _currentMembers.FirstOrDefault(m => m.UserId == message.SenderUserId.Value);
            if (emp != null)
            {
                ShowMemberDetailWindow(emp);
                e.Handled = true;
            }
        }
    }

    // 채팅 아바타 클릭 → 직원 상세를 별도 창(MemberDetailWindow)으로. 이미 열려 있으면 갱신.
    private void ShowMemberDetailWindow(EmployeeModel emp)
    {
        if (_memberDetailWindow != null)
        {
            _memberDetailWindow.UpdateMember(emp);
            _memberDetailWindow.Activate();
            return;
        }
        var win = new MemberDetailWindow(emp, "참여자 정보", _myUserId) { Owner = this };
        win.Left = this.Left + this.Width + 6;
        win.Top = this.Top;
        win.DirectChatRequested += target => OpenDirectChatRequested?.Invoke(target.UserId);
        win.Closed += (_, _) => { if (ReferenceEquals(_memberDetailWindow, win)) _memberDetailWindow = null; };
        _memberDetailWindow = win;
        win.Show();
    }

    private void ShowMemberDetail(EmployeeModel emp)
    {
        txtMemberName.Text = emp.Name;
        txtMemberDept.Text = emp.TeamName;
        txtMemberJob.Text = string.IsNullOrEmpty(emp.JobTitle) ? "-" : emp.JobTitle;
        txtMemberExt.Text = string.IsNullOrEmpty(emp.ExtensionNumber) ? "-" : emp.ExtensionNumber;
        txtMemberMobile.Text = string.IsNullOrEmpty(emp.MobileNumber) ? "-" : emp.MobileNumber;
        txtMemberEmail.Text = string.IsNullOrEmpty(emp.Email) ? "-" : emp.Email;
        WebImage.SetUrl(imgMemberDetail, !string.IsNullOrEmpty(emp.ProfileImageFilename) ? emp.ProfileImageUrl : null);
        memberDetailPanel.Visibility = Visibility.Visible;
    }

    private void CloseMemberDetail_Click(object sender, RoutedEventArgs e) => memberDetailPanel.Visibility = Visibility.Collapsed;

    private void OpenMembers_Click(object sender, RoutedEventArgs e) => membersModal.Visibility = Visibility.Visible;

    private void OpenMemberImage_Click(object sender, MouseButtonEventArgs e)
    {
        var url = WebImage.GetUrl(imgMemberDetail);
        if (!string.IsNullOrEmpty(url))
        {
            ImagePreviewWindow.Show(this, url);
            e.Handled = true;
        }
    }

    private void ParticipantsBar_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenParticipantsWindow();
    }

    private void ShowMembers_Click(object sender, MouseButtonEventArgs e)
    {
        OpenParticipantsWindow();
    }

    private void OpenParticipantsWindow()
    {
        if (_participantsWindow != null)
        {
            if (_participantsWindow.WindowState == WindowState.Minimized) _participantsWindow.WindowState = WindowState.Normal;
            _participantsWindow.Activate();
            return;
        }

        _participantsWindow = new ParticipantsWindow(_currentMembers, _myUserId) { Owner = this };
        _participantsWindow.DirectChatRequested += target => OpenDirectChatRequested?.Invoke(target.UserId);
        // 좌측 공간이 충분하면 좌측, 아니면 우측에 배치
        double leftCandidate = this.Left - _participantsWindow.Width - 6;
        if (leftCandidate >= 0)
        {
            _participantsWindow.Left = leftCandidate;
        }
        else
        {
            _participantsWindow.Left = this.Left + this.Width + 6;
        }
        _participantsWindow.Top = this.Top;
        _participantsWindow.Closed += (_, _) => _participantsWindow = null;
        _participantsWindow.Show();
    }

    private void CloseMembers_Click(object sender, RoutedEventArgs e)
    {
        memberDetailPanel.Visibility = Visibility.Collapsed;
        membersModal.Visibility = Visibility.Collapsed;
    }

    private void InviteRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is EmployeeModel emp)
        {
            emp.IsSelected = !emp.IsSelected;
        }
    }

    private List<OrgNodeModel> BuildInviteOrgTree(IEnumerable<OrgNodeModel> nodes, Dictionary<long, EmployeeModel> candidates)
    {
        return nodes.Select(n => CloneInviteOrgNode(n, candidates)).Where(n => n != null).Cast<OrgNodeModel>().ToList();
    }

    private List<EmployeeModel> FlattenOrgMembers(IEnumerable<OrgNodeModel> nodes)
    {
        var list = new List<EmployeeModel>();
        foreach (var node in nodes)
        {
            list.AddRange(node.Members);
            list.AddRange(FlattenOrgMembers(node.Children));
        }
        return list;
    }

    private OrgNodeModel MapOrgNode(JsonElement n)
    {
        return new OrgNodeModel
        {
            Name = n.TryGetProperty("name", out var name) ? name.GetString() : "",
            NodeType = n.TryGetProperty("nodeType", out var nt) ? nt.GetString() : "TEAM",
            Members = n.TryGetProperty("members", out var members)
                ? members.EnumerateArray().Select(MapOrgEmployee).ToList()
                : new List<EmployeeModel>(),
            Children = n.TryGetProperty("children", out var children)
                ? children.EnumerateArray().Select(MapOrgNode).ToList()
                : new List<OrgNodeModel>()
        };
    }

    private EmployeeModel MapOrgEmployee(JsonElement m)
    {
        return new EmployeeModel
        {
            UserId = m.TryGetProperty("userId", out var userId) ? userId.GetInt64() : 0,
            Name = m.TryGetProperty("name", out var name) ? name.GetString() : null,
            JobTitle = m.TryGetProperty("jobTitle", out var jobTitle) ? jobTitle.GetString() : null,
            TeamName = m.TryGetProperty("teamName", out var teamName) ? teamName.GetString() : null,
            Email = m.TryGetProperty("email", out var email) ? email.GetString() : null,
            MobileNumber = m.TryGetProperty("mobileNumber", out var mobile) ? mobile.GetString() : null,
            ExtensionNumber = m.TryGetProperty("extensionNumber", out var ext) ? ext.GetString() : null,
            ProfileImageFilename = m.TryGetProperty("profileImageFilename", out var profile) ? profile.GetString() : null
        };
    }

    private OrgNodeModel? CloneInviteOrgNode(OrgNodeModel node, Dictionary<long, EmployeeModel> candidates)
    {
        var members = node.Members
            .Select(m => candidates.TryGetValue(m.UserId, out var candidate) ? candidate : null)
            .Where(m => m != null)
            .Cast<EmployeeModel>()
            .ToList();
        var children = node.Children.Select(c => CloneInviteOrgNode(c, candidates)).Where(c => c != null).Cast<OrgNodeModel>().ToList();
        if (members.Count == 0 && children.Count == 0) return null;
        return new OrgNodeModel
        {
            Name = node.Name,
            NodeType = node.NodeType,
            IsExpanded = false,
            Members = members,
            Children = children
        };
    }

    private IEnumerable<EmployeeModel> CollectInviteMembers(OrgNodeModel node)
    {
        foreach (var member in node.Members)
        {
            if (member.UserId != _myUserId) yield return member;
        }
        foreach (var child in node.Children)
        {
            foreach (var member in CollectInviteMembers(child)) yield return member;
        }
    }

    private void InviteOrgNodeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not OrgNodeModel node) return;
        node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
        trvInviteOrg.Items.Refresh();
    }

    private void InviteOrgNodeSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not OrgNodeModel node) return;
        var members = CollectInviteMembers(node).DistinctBy(emp => emp.UserId).ToList();
        bool selectAll = members.Any(emp => !emp.IsSelected);
        foreach (var member in members)
        {
            member.IsSelected = selectAll;
        }
        e.Handled = true;
        trvInviteOrg.Items.Refresh();
        lstInviteMembers.Items.Refresh();
    }

    private async void InviteMembers_Click(object sender, RoutedEventArgs e)
    {
        var selectedIds = _inviteCandidates.Where(emp => emp.IsSelected).Select(emp => emp.UserId).ToList();
        if (selectedIds.Count == 0) return;

        if (await _apiService.InviteMembersAsync(_roomId, selectedIds))
        {
            inviteModal.Visibility = Visibility.Collapsed;
            await RefreshChatRoom();
        }
    }

    private async void LeaveRoom_Click(object sender, RoutedEventArgs e)
    {
        if (await _apiService.LeaveRoomAsync(_roomId))
        {
            RoomLeft?.Invoke(_roomId);
            Close();
        }
    }

    private void TxtInviteSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string query = txtInviteSearch.Text.Trim().ToLower();
        lstInviteMembers.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _inviteCandidates
            : _inviteCandidates.Where(emp =>
                (emp.Name?.ToLower().Contains(query) ?? false) ||
                (emp.TeamName?.ToLower().Contains(query) ?? false) ||
                (emp.JobTitle?.ToLower().Contains(query) ?? false)).ToList();
    }

    private async void RenameRoom_Click(object sender, RoutedEventArgs e)
    {
        var name = ShowTextInputDialog("채팅방 이름 변경", "내 화면에 표시할 채팅방 이름을 입력하세요.", _roomSettings.CustomTitle ?? _roomSettings.Title ?? "");
        if (name == null) return;
        _roomSettings.CustomTitle = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        await SaveRoomSettingsAsync();
    }

    private async void ToggleMute_Click(object sender, RoutedEventArgs e)
    {
        _roomSettings.Muted = !_roomSettings.Muted;
        ApplyRoomSettings(_roomSettings, false);
        await SaveRoomSettingsAsync();
    }

    private async void TogglePin_Click(object sender, RoutedEventArgs e)
    {
        _roomSettings.Pinned = !_roomSettings.Pinned;
        ApplyRoomSettings(_roomSettings, false);
        await SaveRoomSettingsAsync();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 창 로드 전(InitializeComponent 중 메뉴 슬라이더 초기화) 발생하는 값 변경은 무시한다.
        // (이때 아직 연결되지 않은 헤더 요소 접근으로 NRE 발생 → 채팅방 열 때 앱 종료)
        if (_applyingOpacity || !IsLoaded) return;
        _roomSettings.OpacityPercent = (int)Math.Round(sldOpacity.Value);
        if (lblOpacity != null) lblOpacity.Text = $"{_roomSettings.OpacityPercent}%";
        ApplyRoomSettings(_roomSettings, false);
        _opacitySaveTimer.Stop();
        _opacitySaveTimer.Start();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / 1024d / 1024d:0.#}MB";
        if (bytes >= 1024L) return $"{Math.Max(1, bytes / 1024)}KB";
        return $"{bytes}B";
    }

    private async Task SaveRoomSettingsAsync()
    {
        var updated = await _apiService.UpdateRoomSettingsAsync(_roomId, _roomSettings.CustomTitle, _roomSettings.Pinned, _roomSettings.Muted, _roomSettings.OpacityPercent);
        if (updated == null) return;
        _roomSettings.Apply(updated);
        ApplyRoomSettings(_roomSettings, true);
        RoomSettingsChanged?.Invoke(_roomSettings);
    }

    private string? ShowTextInputDialog(string title, string message, string initialValue)
    {
        var dialog = new TextInputDialogWindow(title, message, initialValue) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }
}

public class PendingChatAttachment
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsImage { get; init; }
    public string PreviewPath => Path;
    public string ShortName => Name.Length <= 8 ? Name : Name[..8] + "...";

    public static PendingChatAttachment FromPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return new PendingChatAttachment
        {
            Path = path,
            Name = System.IO.Path.GetFileName(path),
            IsImage = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp"
        };
    }
}

public class MessageModelComparer : IEqualityComparer<MessageModel>
{
    public bool Equals(MessageModel? x, MessageModel? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Id == y.Id && x.Body == y.Body && (x.Attachments?.Count ?? 0) == (y.Attachments?.Count ?? 0);
    }
    public int GetHashCode(MessageModel obj) => obj.Id.GetHashCode();
}
