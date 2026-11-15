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

// [W6] MainWindow 분리 — 로그인·OTP·서버 주소 설정·SSO 웹 이동·원격 로그아웃. 동작 변경 없음(파일 분리만).
public partial class MainWindow
{
    private async Task HandleRemoteLogoutAsync(string? message, bool kicked)
    {
        if (_remoteLogoutInProgress || loginView.Visibility == Visibility.Visible) return;
        _remoteLogoutInProgress = true;

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();

        _refreshTimer.Stop();
        message = kicked
            ? "다른 곳에서 접속하여, 연결이 끊어집니다."
            : (string.IsNullOrWhiteSpace(message) ? "로그인 세션이 만료되었습니다. 다시 로그인해 주세요." : message);

        await ShowCustomModal("알림", message, false);

        _settingsService.ClearToken();
        await _apiService.LogoutAsync();
        HandleLogout();
    }

    private void OpenServerConfig_Click(object sender, RoutedEventArgs e)
    {
        txtServerUrl.Text = _settingsService.Load().ServerUrl;
        serverConfigModal.Visibility = Visibility.Visible;
        txtServerUrl.Focus();
    }

    private void CloseServerConfig_Click(object sender, RoutedEventArgs e)
    {
        serverConfigModal.Visibility = Visibility.Collapsed;
    }

    private async void SaveServerConfig_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();
        // 끝의 '/'를 제거해 프로필 이미지 URL이 '//'로 생성되는 경우를 막는다.
        settings.ServerUrl = txtServerUrl.Text.Trim().TrimEnd('/');
        _settingsService.Save(settings);
        _apiService.UpdateServerUrl(settings.ServerUrl);
        EmployeeModel.UpdateBaseUrl(settings.ServerUrl);
        serverConfigModal.Visibility = Visibility.Collapsed;
        
        if (mainView.Visibility == Visibility.Visible) {
            await ShowCustomModal("알림", "서버 주소가 변경되었습니다. 변경사항을 적용하려면 다시 로그인해 주세요.", false);
            Logout_Click(sender, e);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        string username = txtUsername.Text;
        string password = txtPassword.Password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            await ShowCustomModal("알림", "아이디와 비밀번호를 입력하세요.", false);
            return;
        }

        ShowLoading("로그인 중...");
        LoginResult login = await _apiService.LoginAsync(username, password);
        HideLoading();

        if (login.HasConflict)
        {
            string existingDevice = string.IsNullOrWhiteSpace(login.ExistingDevice) ? "다른 PC" : login.ExistingDevice!;
            bool force = await ShowCustomModal("로그인 확인",
                $"이미 {existingDevice}에서 로그인 중입니다.\n해당 연결을 끊고 이 PC에서 로그인하시겠습니까?", true);
            if (!force) return;

            ShowLoading("기존 연결 정리 중...");
            login = await _apiService.LoginAsync(username, password, true);
            HideLoading();
        }

        if (login.Success)
        {
            var settings = _settingsService.Load();
            settings.Username = username;
            settings.RememberLogin = chkRememberLogin.IsChecked == true;
            settings.DeviceToken = settings.RememberLogin ? _apiService.LastDeviceToken : null;
            _settingsService.Save(settings);
            await HandleLoginSuccess();
        }
        // [W3] 네트워크 오류는 자격증명 오류와 구분해 안내
        else await ShowCustomModal("오류", login.NetworkError
            ? "서버에 연결할 수 없습니다. 네트워크 상태를 확인하거나 잠시 후 다시 시도해 주세요."
            : "로그인 정보가 올바르지 않습니다.", false);
    }

    private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Login_Click(sender, e);
    }

    private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        txtPasswordPlaceholder.Visibility = string.IsNullOrEmpty(txtPassword.Password) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task HandleLoginSuccess()
    {
        // 2차 인증(OTP) 필수 계정은 코드 검증/등록 전까지 메인 진입 차단.
        if (!await EnsureOtpAsync())
        {
            _settingsService.ClearToken();
            await _apiService.LogoutAsync();
            HandleLogout();
            return;
        }
        if (_apiService.MustChangePassword)
        {
            MarkReadyToShow();
            var change = new PasswordChangeWindow(_apiService) { Owner = this };
            if (change.ShowDialog() != true)
            {
                _settingsService.ClearToken();
                await _apiService.LogoutAsync();
                HandleLogout();
                return;
            }
        }
        _remoteLogoutInProgress = false;
        SwitchToMain();
        await _apiService.InitializeAuthenticatedSessionAsync();
        ShowLoading("데이터 초기화 중 (0/4)");
        
        try {
            txtLoadingStatus.Text = "데이터 초기화 중 (1/4)";
            var profileTask = _apiService.GetCurrentUserProfileAsync();
            var attendanceTask = _apiService.GetTodayAttendanceAsync();
            var panelTask = _apiService.GetRightPanelDataAsync();

            await Task.WhenAll(profileTask, attendanceTask, panelTask);

            txtLoadingStatus.Text = "데이터 초기화 중 (2/4)";
            var profile = await profileTask;
            if (profile != null) {
                _myUserId = profile.UserId;
                _myTeamName = profile.TeamName;
                txtUserName.Text = profile.Name;
                txtUserDept.Text = profile.TeamName;
                _myProfileImageUrl = profile.HasProfileImage
                    ? $"{profile.ProfileImageUrl}?login={DateTime.UtcNow.Ticks}"
                    : null;
                WebImage.SetUrl(imgUserProfile, _myProfileImageUrl);
            }

            txtLoadingStatus.Text = "데이터 초기화 중 (3/4)";
            UpdateAttendanceUI(await attendanceTask);
            var panelData = await panelTask;
            if (panelData.HasValue) ProcessPanelData(panelData.Value);
            txtLoadingStatus.Text = "데이터 초기화 중 (4/4)";
            await LoadCalendarMonthAsync();
            await LoadDesktopDocumentsAsync(_documentBox);

        } catch (Exception ex) {
            Console.WriteLine("Init Error: " + ex.Message);
        }

        HideLoading();
        _refreshTimer.Start();
    }

    /// <summary>OTP 필수 계정이면 검증/등록 모달을 띄우고 통과 여부 반환. 미필수면 즉시 true.</summary>
    private async Task<bool> EnsureOtpAsync()
    {
        OtpStateResult? state;
        try { state = await _apiService.GetOtpStateAsync(); }
        catch { return true; } // 상태 조회 실패 시 막지 않음(서버 구버전 등)
        if (state == null || !state.Required) return true;

        // [중요] 자동로그인 시작 경로에서는 아직 스플래시가 떠 있어 OTP 모달을 가린다.
        // 모달 표시 직전에 스플래시를 닫고 창을 표시한다(가려서 멈춘 것처럼 보이던 문제 수정).
        MarkReadyToShow();

        txtOtpErr.Visibility = Visibility.Collapsed;
        ClearOtpBoxes();
        _otpEnrollMode = !state.Enrolled;
        if (_otpEnrollMode)
        {
            OtpSetupResult? setup = null;
            try { setup = await _apiService.GetOtpSetupAsync(); } catch { }
            txtOtpMsg.Text = "이 계정은 2차 인증이 필요합니다. OTP 앱으로 QR을 스캔한 뒤 6자리 코드를 입력하세요.";
            imgOtpQr.Source = setup == null ? null : LoadDataUriImage(setup.QrDataUri);
            otpQrBox.Visibility = imgOtpQr.Source == null ? Visibility.Collapsed : Visibility.Visible;
            txtOtpKey.Text = setup == null ? "" : ("수동 키: " + setup.Secret);
            txtOtpKey.Visibility = setup == null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            txtOtpMsg.Text = "인증 앱의 6자리 코드를 입력하세요.";
            otpQrBox.Visibility = Visibility.Collapsed;
            txtOtpKey.Visibility = Visibility.Collapsed;
        }
        otpModal.Visibility = Visibility.Visible;
        otpBox1.Focus();
        _otpTcs = new TaskCompletionSource<bool>();
        return await _otpTcs.Task;
    }

    // [1.0.22] 6자리 분리 입력 — 입력 시 다음 칸 포커스, 6자리 완성 시 자동 검증, 붙여넣기 분배.
    private TextBox[] OtpBoxes() => new[] { otpBox1, otpBox2, otpBox3, otpBox4, otpBox5, otpBox6 };
    private bool _otpSubmitting;

    private void ClearOtpBoxes()
    {
        _otpSubmitting = true;
        foreach (var b in OtpBoxes())
        {
            b.Text = "";
            b.IsEnabled = true;
        }
        ResetOtpAnim();
        txtOtpBusy.Visibility = Visibility.Collapsed;
        _otpSubmitting = false;
    }

    private string CollectOtpCode() => string.Concat(OtpBoxes().Select(b => (b.Text ?? "").Trim()));

    private void OtpBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_otpSubmitting) return;
        var boxes = OtpBoxes();
        var box = (TextBox)sender;
        var text = new string((box.Text ?? "").Where(char.IsDigit).ToArray());
        if (text != box.Text)
        {
            box.Text = text;
            box.CaretIndex = text.Length;
            if (text.Length == 0) return;
        }
        // 붙여넣기(여러 자리)면 현재 칸부터 분배
        int idx = Array.IndexOf(boxes, box);
        if (text.Length > 1)
        {
            for (int i = 0; i < text.Length && idx + i < boxes.Length; i++) boxes[idx + i].Text = text[i].ToString();
            box.Text = text[0].ToString();
            int next = Math.Min(idx + text.Length, boxes.Length - 1);
            boxes[next].Focus(); boxes[next].CaretIndex = 1;
        }
        else if (text.Length == 1 && idx < boxes.Length - 1)
        {
            boxes[idx + 1].Focus();
        }
        var code = CollectOtpCode();
        if (code.Length == 6) _ = SubmitOtpAsync(code);
    }

    private void OtpBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var boxes = OtpBoxes();
        var box = (TextBox)sender;
        int idx = Array.IndexOf(boxes, box);
        if (e.Key == System.Windows.Input.Key.Back && string.IsNullOrEmpty(box.Text) && idx > 0)
        {
            boxes[idx - 1].Text = "";
            boxes[idx - 1].Focus();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Left && idx > 0) { boxes[idx - 1].Focus(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Right && idx < boxes.Length - 1) { boxes[idx + 1].Focus(); e.Handled = true; }
    }

    private async Task SubmitOtpAsync(string code)
    {
        if (_otpSubmitting) return;
        _otpSubmitting = true;
        txtOtpErr.Visibility = Visibility.Collapsed;
        txtOtpResult.Visibility = Visibility.Collapsed;
        foreach (var b in OtpBoxes()) b.IsEnabled = false;

        // 1) 6칸 수렴 + 로딩 스피너 시작
        StartOtpConverge();
        var loadStart = DateTime.UtcNow;

        // 2) 서버 검증
        bool ok;
        try { ok = _otpEnrollMode ? await _apiService.EnrollOtpAsync(code) : await _apiService.VerifyOtpAsync(code); }
        catch { ok = false; }

        // 3) 로딩을 최소 1.5초 노출(연출)
        var elapsed = (DateTime.UtcNow - loadStart).TotalMilliseconds;
        if (elapsed < 1500) await Task.Delay((int)(1500 - elapsed));

        // 4) 성공/실패 표시
        ShowOtpResult(ok);
        if (ok)
        {
            await Task.Delay(950);
            otpModal.Visibility = Visibility.Collapsed;
            ResetOtpAnim();
            ClearOtpBoxes();
            _otpTcs?.TrySetResult(true);
        }
        else
        {
            await Task.Delay(1150);
            ResetOtpAnim();
            foreach (var b in OtpBoxes()) b.IsEnabled = true;
            _otpSubmitting = false;
            OtpError("코드가 올바르지 않습니다. 휴대폰 시간을 확인하세요.");
        }
    }

    /// <summary>[1.0.27] 6칸을 중앙으로 수렴시키고 로딩 스피너를 회전시킨다.</summary>
    private void StartOtpConverge()
    {
        var dur = TimeSpan.FromMilliseconds(450);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        otpBoxesScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.12, dur) { EasingFunction = ease });
        otpBoxesPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, dur));
        otpSpinner.Visibility = Visibility.Visible;
        otpCheck.Visibility = Visibility.Collapsed;
        otpCross.Visibility = Visibility.Collapsed;
        otpAnimChip.Visibility = Visibility.Visible;
        otpAnimChip.Opacity = 0;
        otpAnimChip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(250) });
        otpSpinRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>[1.0.27] 스피너 정지 후 체크/X + 성공/실패 텍스트.</summary>
    private void ShowOtpResult(bool ok)
    {
        otpSpinRot.BeginAnimation(RotateTransform.AngleProperty, null);
        otpSpinner.Visibility = Visibility.Collapsed;
        if (ok)
        {
            otpCheck.Visibility = Visibility.Visible;
            txtOtpResult.Text = "성공";
            txtOtpResult.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
        }
        else
        {
            otpCross.Visibility = Visibility.Visible;
            txtOtpResult.Text = "실패";
            txtOtpResult.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
        txtOtpResult.Opacity = 0;
        txtOtpResult.Visibility = Visibility.Visible;
        txtOtpResult.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>[1.0.27] 애니메이션 상태 초기화(박스 복원).</summary>
    private void ResetOtpAnim()
    {
        otpBoxesScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        otpBoxesScale.ScaleX = 1;
        otpBoxesPanel.BeginAnimation(UIElement.OpacityProperty, null);
        otpBoxesPanel.Opacity = 1;
        otpAnimChip.Visibility = Visibility.Collapsed;
        otpSpinner.Visibility = Visibility.Visible;
        otpCheck.Visibility = Visibility.Collapsed;
        otpCross.Visibility = Visibility.Collapsed;
        txtOtpResult.Visibility = Visibility.Collapsed;
    }

    private void OtpError(string msg)
    {
        txtOtpErr.Text = msg; txtOtpErr.Visibility = Visibility.Visible;
        ClearOtpBoxes();
        otpBox1.Focus();
    }

    private void OtpLogout_Click(object sender, RoutedEventArgs e)
    {
        otpModal.Visibility = Visibility.Collapsed;
        _otpTcs?.TrySetResult(false);
    }

    private async void OpenFullSystem_Click(object sender, RoutedEventArgs e)
    {
        // 배포시스템 연동 사용자는 [오피스 / 배포] 2단 메뉴, 아니면 오피스 바로 열기.
        bool deployLinked = await _apiService.IsDeploySsoLinkedAsync();
        if (!deployLinked)
        {
            await OpenWebRedirectAsync("/dashboard");
            return;
        }
        var menu = new System.Windows.Controls.ContextMenu();
        var office = new System.Windows.Controls.MenuItem { Header = "WorkHub Desktop 열기" };
        office.Click += async (_, __) => await OpenWebRedirectAsync("/dashboard");
        var deploy = new System.Windows.Controls.MenuItem { Header = "배포 시스템 열기" };
        deploy.Click += async (_, __) => await OpenWebRedirectAsync("/deploy-system/sso");
        menu.Items.Add(office);
        menu.Items.Add(deploy);
        menu.PlacementTarget = btnOpenWeb;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>desktop SSO 토큰으로 지정 redirect 경로를 기본 브라우저에 연다(오피스=/dashboard, 배포=/deploy-system/sso).</summary>
    private async Task OpenWebRedirectAsync(string redirect)
    {
        ShowLoading("웹 이동 준비 중 (0/2)");
        var url = await _apiService.CreateSsoUrlAsync(redirect);
        if (string.IsNullOrEmpty(url))
        {
            txtLoadingStatus.Text = "웹 이동 재시도 중 (1/2)";
            await Task.Delay(400);
            url = await _apiService.CreateSsoUrlAsync(redirect);
        }
        txtLoadingStatus.Text = "웹 이동 준비 중 (2/2)";
        HideLoading();
        if (string.IsNullOrEmpty(url)) { await ShowCustomModal("오류", "웹 시스템 접속에 실패했습니다. 서비스를 재시작한 뒤 다시 시도해 주세요.", false); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

}
