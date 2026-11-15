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

public partial class MainWindow : Window
{
    private readonly WorkSystemApiService _apiService = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly AutoUpdateService _autoUpdateService = new();
    private System.Windows.Threading.DispatcherTimer? _updateCheckTimer;
    private bool _updateSuppressedThisSession;
    private string _updateServerUrl = "";
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private bool _isExplicitExit = false;
    private System.Drawing.Icon? _trayAssociatedIcon;
    private readonly Dictionary<long, ChatRoomWindow> _chatWindows = new();
    private List<OrgNodeModel> _fullOrgTree = new();
    private List<EmployeeModel> _allEmployees = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<MessageRoomModel> _rooms = new();
    private bool _roomsBound = false;
    private EmployeeModel? _selectedEmployee;
    private System.Windows.Threading.DispatcherTimer? _notifClickTimer;
    private NotificationGroup? _pendingNotifGroup;
    private MemberDetailWindow? _memberDetailWindow;
    private long _myUserId = 0;
    private string? _myTeamName;
    private string? _myProfileImageUrl;
    private AttendanceRecord? _currentAttendance;
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedCalendarDate = DateTime.Today;
    private List<CompanyScheduleModel> _calendarSchedules = new();
    private string _documentBox = "all";
    private List<DesktopDocumentModel> _desktopDocumentsCache = new();
    private List<OrgNodeModel> _inviteOrgTree = new();
    private bool _remoteLogoutInProgress = false;

    private const double MAIN_HEIGHT = 820;
    private const double LOGIN_HEIGHT = 540;

    private TaskCompletionSource<bool>? _modalTcs;
    public event Action? ReadyToShow;
    private bool _readyShown;

    // 스플래시를 닫고 창을 화면 중앙에 표시한다(1회만). OTP 모달처럼 시작(자동로그인) 중
    // 사용자 입력이 필요한 경우, 모달이 스플래시에 가리지 않도록 모달 표시 직전에 호출한다.
    private void MarkReadyToShow()
    {
        EnsureShownCentered();
        if (_readyShown) return;
        _readyShown = true;
        ReadyToShow?.Invoke();
    }
    public Action<string>? StartupStatusChanged { get; set; }
    public Action<SystemThemeModel>? StartupThemeChanged { get; set; }

    public MainWindow()
    {
        TryCompletePendingUpdaterReplacement();
        InitializeComponent();
        ApplyTrayIconFromExecutable();
        this.Visibility = Visibility.Collapsed;
        SetupEvents();
        SetupTimer();
        Loaded += MainWindow_Loaded;
        SourceInitialized += (_, _) => ApplyNativeWindowFrame();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closed += (_, _) => _trayAssociatedIcon?.Dispose();
        Activated += async (_, _) => await RefreshAttendanceAsync();
    }

    private void ApplyNativeWindowFrame()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int cornerPreference = DwmWindowCornerPreference.Round;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmWindowAttribute.WindowCornerPreference,
                ref cornerPreference,
                sizeof(int));

            int borderColor = unchecked((int)0xFFFFFFFE); // COLORREF_NONE
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmWindowAttribute.BorderColor,
                ref borderColor,
                sizeof(int));
        }
        catch
        {
        }
    }

    private static class DwmWindowAttribute
    {
        public const int WindowCornerPreference = 33;
        public const int BorderColor = 34;
    }

    private static class DwmWindowCornerPreference
    {
        public const int Round = 2;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private void ApplyTrayIconFromExecutable()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath)) return;

        _trayAssociatedIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        if (_trayAssociatedIcon != null)
        {
            trayIcon.Icon = _trayAssociatedIcon;
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (customModal.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Enter)
            {
                ModalYes_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (btnModalNo.Visibility == Visibility.Visible)
                    ModalNo_Click(sender, e);
                else
                    ModalYes_Click(sender, e);
                e.Handled = true;
            }
        }
        else if (serverConfigModal.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Enter)
            {
                SaveServerConfig_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseServerConfig_Click(sender, e);
                e.Handled = true;
            }
        }
        else if (chatInviteModal.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseInvite_Click(sender, null!);
                e.Handled = true;
            }
        }
    }

    private void SetupEvents()
    {
        _apiService.OnNotificationReceived += notification => Dispatcher.Invoke(() => ShowToast(notification));
        _apiService.OnPanelDataUpdated += () => Dispatcher.Invoke(RefreshPanelData);
        _apiService.OnWebSocketConnected += () => Dispatcher.Invoke(() =>
        {
            RefreshPanelData();
            _ = RefreshAttendanceAsync();
        });
        _apiService.OnPanelDataSnapshotUpdated += data => Dispatcher.Invoke(() => ProcessPanelData(data));
        _apiService.OnPresenceChanged += ids => Dispatcher.Invoke(() => ApplyPresence(ids));
        _apiService.OnMessageCreated += payload => Dispatcher.Invoke(() => ApplyImmediateRoomPreview(payload));
        // 프로필 사진은 목록을 다시 그리지 않고 해당 항목만 갱신한다.
        _apiService.OnProfileChanged += (uid, fn) => Dispatcher.Invoke(() => ApplyProfileChanged(uid, fn));
        ToastNotificationManagerCompat.OnActivated += args =>
        {
            Dispatcher.Invoke(async () =>
            {
                var values = ParseToastArguments(args.Argument);
                if (values.TryGetValue("notificationId", out var idValue) && long.TryParse(idValue, out var id))
                {
                    values.TryGetValue("link", out var link);
                    await OpenNotificationAsync(new NotificationModel { Id = id, LinkUrl = link });
                }
            });
        };
        _apiService.OnSessionExpired += () => Dispatcher.Invoke(async () =>
        {
            await HandleRemoteLogoutAsync("로그인 세션이 만료되었습니다. 다시 로그인해 주세요.", false);
        });
        _apiService.OnKicked += message => Dispatcher.Invoke(async () =>
        {
            await HandleRemoteLogoutAsync(message, true);
        });
    }

    private void SetupTimer()
    {
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (s, e) =>
        {
            txtCurrentTime.Text = DateTime.Now.ToString("HH:mm:ss");
            txtTodayDate.Text = DateTime.Now.ToString("yyyy.MM.dd dddd");
            if (mainView.Visibility == Visibility.Visible && _attendanceDate != DateTime.Today)
            {
                UpdateAttendanceUI(null);
                if ((DateTime.Now - _attendanceLastAttempt).TotalSeconds >= 10)
                    _ = RefreshAttendanceAsync();
            }
            UpdateAttendanceStatusText();
        };
        _timer.Start();

        txtTodayDate.Text = DateTime.Now.ToString("yyyy.MM.dd dddd");

        // WebSocket 이벤트가 누락될 때를 대비해 60초마다 보조 갱신한다.
        _refreshTimer.Interval = TimeSpan.FromSeconds(60);
        _refreshTimer.Tick += async (s, e) => {
            // WebSocket is the immediate path; polling remains as a safety net when events are missed.
            RefreshPanelData();
            await RefreshAttendanceAsync();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartupDiagnostics.Snapshot(this, "main.startup.enter");
        // [E-13/A] 어떤 await가 멈춰도 창은 무조건 화면 안에 보이게 — 진입 즉시 화면 중앙으로 이동/표시.
        // App.xaml.cs가 시작 시 창을 -32000(화면 밖)에 둬서 검은 깜빡임을 막는데, 그 상태로 Loaded
        // 도중 await가 멈추면 finally가 안 돌아 창이 영영 화면 밖에 갇히는 케이스를 차단한다.
        EnsureShownCentered();
        ApplySidebarIconsForTheme();
        var settings = _settingsService.Load();
        bool autoLoginExpired = false;
        try
        {
            txtUsername.Text = settings.Username ?? "";
            chkRememberLogin.IsChecked = settings.RememberLogin;
            _apiService.UpdateServerUrl(settings.ServerUrl);
            EmployeeModel.UpdateBaseUrl(settings.ServerUrl);
            WebImage.Configure(_apiService.HttpClient);
            if (await CheckForUpdateOnStartup(settings.ServerUrl))
            {
                return; // 업데이트 적용 → 앱 종료 예정 (finally에서 _isExplicitExit 가드로 표시 생략)
            }

            await ApplySystemThemeAsync();
            StartupDiagnostics.Write("main.theme.complete");
            await ApplySystemBrandingAsync(settings.ServerUrl);
            StartupDiagnostics.Write("main.branding.complete");

            if (settings.RememberLogin && !string.IsNullOrEmpty(settings.DeviceToken))
            {
                bool success = await _apiService.AutoLoginAsync(settings.DeviceToken);
                if (success)
                {
                    await HandleLoginSuccess();
                }
                else
                {
                    _settingsService.ClearToken();
                    autoLoginExpired = true;
                    SwitchToLogin();
                }
            }
            else SwitchToLogin();
        }
        catch (Exception error)
        {
            StartupDiagnostics.Write("main.startup.error", error.ToString());
            // 시작 중 예외가 나도 최소한 로그인 화면으로는 진입시켜, 창이 숨은 채 남지 않게 한다.
            try { SwitchToLogin(); } catch { }
        }
        finally
        {
            // [E-13] 어떤 경로/예외에서도 창을 항상 화면 안에 중앙 정렬하여 표시한다.
            // (업데이트 적용으로 종료 중일 때만 생략)
            if (!_isExplicitExit)
            {
                MarkReadyToShow();
            }
        }

        if (_isExplicitExit) return;
        StartPeriodicUpdateCheck(settings.ServerUrl);
        if (autoLoginExpired)
        {
             await ShowCustomModal("알림", "자동 로그인 정보가 만료되었습니다. 다시 로그인해 주세요.", false);
        }
    }

    private void SetStartupStatus(string message)
    {
        StartupStatusChanged?.Invoke(message);
    }

    private void SwitchToLogin()
    {
        initialLoadingView.Visibility = Visibility.Collapsed;
        mainView.Visibility = Visibility.Collapsed;
        loginView.Visibility = Visibility.Visible;
        menuLogout.Visibility = Visibility.Collapsed;
        _myUserId = 0;
        _myTeamName = null;
        _myProfileImageUrl = null;
        WebImage.SetUrl(imgUserProfile, null);
        AnimateHeight(LOGIN_HEIGHT); // 로그인 화면 높이로 복구
    }

    private void SwitchToMain()
    {
        initialLoadingView.Visibility = Visibility.Collapsed;
        loginView.Visibility = Visibility.Collapsed;
        mainView.Visibility = Visibility.Visible;
        menuLogout.Visibility = Visibility.Visible;
        AnimateHeight(MAIN_HEIGHT);
    }

    private void AnimateHeight(double targetHeight)
    {
        if (Math.Abs(this.Height - targetHeight) < 1) return;

        var animation = new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromSeconds(0.4),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
        };
        this.BeginAnimation(Window.HeightProperty, animation);
        
        double diff = targetHeight - this.Height;
        var topAnimation = new DoubleAnimation
        {
            To = this.Top - (diff / 2),
            Duration = TimeSpan.FromSeconds(0.4),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
        };
        this.BeginAnimation(Window.TopProperty, topAnimation);
    }

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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        menuTheme.IsChecked = ThemeService.Current == Portfolio.OfficeSystem.ThemeMode.Dark;
        btnSettings.ContextMenu.PlacementTarget = btnSettings;
        btnSettings.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btnSettings.ContextMenu.IsOpen = true;
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e)
    {
        OpenApp_Click(sender, e);
    }

    private T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null) { if (child is T typed) return typed; child = VisualTreeHelper.GetParent(child); }
        return null;
    }

    private MemberPickerWindow? _activeCreateRoomPicker;
    private async Task<bool> ShowCustomModal(string title, string body, bool showCancel)
    {
        txtModalTitle.Text = title; txtModalBody.Text = body;
        btnModalNo.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        customModal.Visibility = Visibility.Visible;
        btnModalYes.Focus();
        _modalTcs = new TaskCompletionSource<bool>();
        return await _modalTcs.Task;
    }

    private void ModalYes_Click(object sender, RoutedEventArgs e) { customModal.Visibility = Visibility.Collapsed; _modalTcs?.TrySetResult(true); }
    private void ModalNo_Click(object sender, RoutedEventArgs e) { customModal.Visibility = Visibility.Collapsed; _modalTcs?.TrySetResult(false); }

    // ── 2차 인증(OTP) 모달 ──────────────────────────────────────
    private TaskCompletionSource<bool>? _otpTcs;
    private bool _otpEnrollMode;

    private static System.Windows.Media.Imaging.BitmapImage? LoadDataUriImage(string? dataUri)
    {
        try
        {
            if (string.IsNullOrEmpty(dataUri)) return null;
            int comma = dataUri.IndexOf(',');
            string b64 = comma >= 0 ? dataUri.Substring(comma + 1) : dataUri;
            byte[] bytes = Convert.FromBase64String(b64);
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.StreamSource = new System.IO.MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private void ShowLoading(string status)
    {
        prgLoading.IsIndeterminate = true;
        txtLoadingStatus.Text = status;
        loadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoading()
    {
        prgLoading.IsIndeterminate = true;
        loadingOverlay.Visibility = Visibility.Collapsed;
    }

    private string FormatRelativeTime(string dateStr)
    {
        try {
            var dt = DateTime.Parse(dateStr); var span = DateTime.Now - dt;
            if (span.TotalMinutes < 1) return "방금 전";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}분 전";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}시간 전";
            return dt.ToString("MM.dd");
        } catch { return ""; }
    }

    private void HandleLogout()
    {
        _refreshTimer.Stop(); CloseChatWindows(); SwitchToLogin();
        _attendanceDate = DateTime.MinValue;
        UpdateAttendanceUI(null);
        employeeDetailModal.Visibility = Visibility.Collapsed; customModal.Visibility = Visibility.Collapsed;
        chatInviteModal.Visibility = Visibility.Collapsed; txtPassword.Password = "";
        txtPasswordPlaceholder.Visibility = Visibility.Visible;
        _myUserId = 0;
        _myTeamName = null;
        _myProfileImageUrl = null;
        txtUserName.Text = "사용자";
        txtUserDept.Text = "본부 · 팀";
        WebImage.SetUrl(imgUserProfile, null);
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (loginView.Visibility == Visibility.Visible) return;
        if (!await ShowCustomModal("로그아웃", "현재 계정에서 로그아웃하시겠습니까?", true)) return;
        ShowLoading("로그아웃 중...");
        var token = _settingsService.Load().DeviceToken ?? _apiService.LastDeviceToken;
        await _apiService.LogoutAsync(token); _settingsService.ClearToken();
        HideLoading(); HandleLogout();
    }

    private void CloseChatWindows() { foreach (var window in _chatWindows.Values.ToList()) window.Close(); _chatWindows.Clear(); }

    private string? ShowTextInputDialog(string title, string message, string initialValue)
    {
        var dialog = new TextInputDialogWindow(title, message, initialValue) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // [P1] 종료 시엔 디바운스 대기 없이 즉시 기록(펜딩 채팅창 좌표 포함)
        _mainPosSavePending = true;
        _posSaveTimer?.Stop();
        FlushPositionSaves();
        if (!_isExplicitExit) { e.Cancel = true; this.Hide(); }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        SaveMainWindowPosition();
    }
    private void OpenApp_Click(object sender, RoutedEventArgs e) { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); }
    private async void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        _isExplicitExit = true;
        // [1.0.84] 앱 종료는 device token 을 revoke 하면 안 됨 — 다음 실행 시 자동 로그인 깨짐.
        // token=null 로 호출하면 /api/desktop/logout 은 건너뛰고 /logout(웹 세션 만료)만 수행.
        // 온라인 표시는 WebSocket close + 세션 만료 grace 로 자연스럽게 해제됨.
        try
        {
            var logoutTask = _apiService.LogoutAsync(null);
            await Task.WhenAny(logoutTask, Task.Delay(2000));
        }
        catch { }
        _apiService.Dispose();
        Application.Current.Shutdown();
    }

    // ===== 업무전달 =====
    private string _workMemoBox = "received";
    private string? _workMemoQuery;
    private List<WorkMemoModel> _workMemos = new();
    private WorkMemoDetailWindow? _workMemoDetailWindow;
    private DispatcherTimer? _workMemoSearchDebouncer;

}
