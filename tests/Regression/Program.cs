using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Portfolio.OfficeSystem;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "ControlStyles", "Tokens", "Light" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Portfolio.OfficeSystem;component/Themes/{resource}.xaml") });
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { await VerifyAsync(); Console.WriteLine("PASS: all Windows regression checks"); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { app.Shutdown(); }
        });
        app.Run();
    }

    private static async Task VerifyAsync()
    {
        VerifyRenderingPreferences();
        using var server = new DesktopServer();
        using var api = new WorkSystemApiService();
        api.UpdateServerUrl(server.Url);
        int expired = 0;
        int connections = 0;
        api.OnSessionExpired += () => Interlocked.Increment(ref expired);
        api.OnWebSocketConnected += () => Interlocked.Increment(ref connections);

        Check((await api.LoginAsync("employee", "initial-password")).Success, "login succeeds");
        Check(api.MustChangePassword, "login reports required password change");
        Check(!server.Paths.Contains("/api/right-panel") && connections == 0 && expired == 0, "login waits for OTP/password before protected requests");
        Check((await api.GetOtpStateAsync())?.Required == true, "OTP state available during onboarding");
        Check(await api.VerifyOtpAsync("123456"), "OTP can be verified before password");
        Check(!(await api.ChangePasswordAsync("wrong", "NewPassword1!", "NewPassword1!")).Success && api.MustChangePassword, "failed password change retains requirement");
        Check((await api.ChangePasswordAsync("initial-password", "NewPassword1!", "NewPassword1!")).Success && !api.MustChangePassword, "password change clears requirement");
        await api.InitializeAuthenticatedSessionAsync();
        await UntilAsync(() => Volatile.Read(ref connections) >= 1);
        Check(server.Paths.Contains("/api/right-panel") && expired == 0, "protected initialization begins after authentication finishes");
        await server.DisconnectFirstAsync();
        await UntilAsync(() => Volatile.Read(ref connections) >= 2);
        Console.WriteLine("PASS: login/OTP/password ordering and real WebSocket reconnect callback");
        await api.LogoutAsync();

        using var auto = new WorkSystemApiService();
        auto.UpdateServerUrl(server.Url);
        int panelBefore = server.Paths.Count(path => path == "/api/right-panel");
        Check(await auto.AutoLoginAsync("saved-token"), "auto-login succeeds");
        Check(auto.MustChangePassword && panelBefore == server.Paths.Count(path => path == "/api/right-panel") && !auto.IsWebSocketConnected, "auto-login waits for onboarding before protected initialization");
        Console.WriteLine("PASS: saved-token login defers protected initialization");
        await auto.LogoutAsync();
        Check(server.RevokedTokens.Contains("saved-token"), "canceling onboarding revokes the remembered device token");
        Check(auto.LastDeviceToken == null && !auto.MustChangePassword, "canceling onboarding clears in-memory credentials");
        Console.WriteLine("PASS: onboarding cancellation revokes the device token");

        // 앱 종료(트레이 '앱 종료')는 기기 토큰을 폐기하지 않는다 — 다음 실행 때 자동 로그인 유지.
        using var exitApi = new WorkSystemApiService();
        exitApi.UpdateServerUrl(server.Url);
        Check(await exitApi.AutoLoginAsync("exit-token"), "auto-login for exit check");
        await exitApi.LogoutAsync(null, revokeToken: false);
        Check(!server.RevokedTokens.Contains("exit-token"), "app exit keeps the remembered device token");
        Console.WriteLine("PASS: app exit keeps the device token");

        // 세션이 끊겨 /login 으로 튕긴 출근 요청은 '성공'이 아니라 실패 + 세션 만료 알림.
        using var expiredApi = new WorkSystemApiService();
        expiredApi.UpdateServerUrl(server.Url);
        int expiredEvents = 0;
        expiredApi.OnSessionExpired += () => Interlocked.Increment(ref expiredEvents);
        server.SessionExpired = true;
        var bounced = await expiredApi.PerformAttendanceAsync("/hr/attendance/check-in");
        server.SessionExpired = false;
        Check(!bounced.Success && expiredEvents == 1, "check-in bounced to the login page is reported as a failure");
        Console.WriteLine("PASS: expired session does not fake a successful check-in");

        // 출퇴근 판정 미리보기·미신청 시간외 건수(1.0.28 서버 응답)
        using var previewApi = new WorkSystemApiService();
        previewApi.UpdateServerUrl(server.Url);
        var preview = await previewApi.GetCheckPreviewAsync("in");
        Check(preview != null && preview.Allowed && preview.Label == "지각", "check-in preview label is read");
        server.CheckedIn = false;
        var today = await previewApi.GetTodayAttendanceAsync();
        Check(today?.UnrequestedOvertimeCount == 2, "unrequested overtime count is read from today payload");
        Console.WriteLine("PASS: attendance preview and unrequested overtime count");

        var window = new MainWindow();
        using var settingsIsolation = new SettingsIsolation(window);
        Field<DispatcherTimer>(window, "_timer").Stop();
        Field<DispatcherTimer>(window, "_refreshTimer").Stop();
        window.Top = 0;
        Invoke(window, "SwitchToLogin");
        Check(((FrameworkElement)window.FindName("initialLoadingView")).Visibility == Visibility.Collapsed
            && ((FrameworkElement)window.FindName("loginView")).Visibility == Visibility.Visible,
            "login view replaces the initial loading view");
        ((FrameworkElement)window.FindName("initialLoadingView")).Visibility = Visibility.Visible;
        Invoke(window, "SwitchToMain");
        Check(((FrameworkElement)window.FindName("initialLoadingView")).Visibility == Visibility.Collapsed
            && ((FrameworkElement)window.FindName("mainView")).Visibility == Visibility.Visible,
            "main view replaces the initial loading view");
        Invoke(window, "SwitchToLogin");
        Console.WriteLine("PASS: login and main view transitions stop the initial loading view");
        foreach (var number in Enumerable.Range(1, 6))
        {
            var box = (TextBox)window.FindName($"otpBox{number}");
            Set(window, "_otpSubmitting", true);
            box.Text = "7";
            box.IsEnabled = false;
        }
        Invoke(window, "ClearOtpBoxes");
        foreach (var number in Enumerable.Range(1, 6))
        {
            var box = (TextBox)window.FindName($"otpBox{number}");
            Check(box.IsEnabled && box.Text.Length == 0, "OTP inputs reusable after successful authentication");
        }
        Console.WriteLine("PASS: six actual WPF OTP fields reset and re-enable");

        var windowApi = Field<WorkSystemApiService>(window, "_apiService");
        windowApi.UpdateServerUrl(server.Url);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var otpDone = new TaskCompletionSource<bool>();
            Set(window, "_otpTcs", otpDone);
            await (Task)Invoke(window, "SubmitOtpAsync", "123456")!;
            Check(await otpDone.Task, "OTP submission completes successfully");
            Check(Enumerable.Range(1, 6).All(number => ((TextBox)window.FindName($"otpBox{number}")).IsEnabled), "successful OTP supports a second authentication without restarting the app");
        }
        Console.WriteLine("PASS: two consecutive successful OTP submissions leave all inputs reusable");
        ((FrameworkElement)window.FindName("mainView")).Visibility = Visibility.Visible;
        Set(window, "_myUserId", 42L);
        Invoke(window, "UpdateAttendanceUI", new AttendanceRecord { CheckedIn = true, CheckedOut = true });
        Set(window, "_attendanceDate", DateTime.Today.AddDays(-1));
        // Execute the actual one-second timer callback, including its date-change refresh.
        typeof(DispatcherTimer).GetMethod("FireTick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Field<DispatcherTimer>(window, "_timer"), null);
        await UntilAsync(() => Field<DateTime>(window, "_attendanceDate") == DateTime.Today);
        Check(((Button)window.FindName("btnCheckIn")).IsEnabled, "new day restores check-in from server state");
        Check(((FrameworkElement)window.FindName("attendanceDonePanel")).Visibility == Visibility.Collapsed, "previous day's completed state cleared");
        server.CheckedIn = true;
        await (Task)Invoke(window, "RefreshAttendanceAsync")!;
        Check(((Button)window.FindName("btnCheckOut")).IsEnabled && !((Button)window.FindName("btnCheckIn")).IsEnabled, "external web check-in reflected by attendance refresh");
        // [OT] 퇴근 후 재출근 — 완료 패널 대신 '시간외 출근' 버튼, 시간외 기록이 열리면 '시간외 퇴근'만 켠다.
        Invoke(window, "UpdateAttendanceUI", new AttendanceRecord { CheckedIn = true, CheckedOut = true, OvertimeReason = "퇴근 후 재출근" });
        Check(((Button)window.FindName("btnCheckIn")).IsEnabled && (string)((Button)window.FindName("btnCheckIn")).Content == "시간외 출근"
              && ((FrameworkElement)window.FindName("attendanceDonePanel")).Visibility == Visibility.Collapsed, "after checkout the app offers overtime check-in");
        Invoke(window, "UpdateAttendanceUI", new AttendanceRecord { CheckedIn = true, CheckedOut = true, OvertimeReason = "퇴근 후 재출근", OvertimeCheckInAt = DateTime.Now.AddMinutes(-5).ToString("s") });
        Check(!((Button)window.FindName("btnCheckIn")).IsEnabled && ((Button)window.FindName("btnCheckOut")).IsEnabled
              && ((TextBlock)window.FindName("txtAttendanceStatus")).Text.StartsWith("시간외 근무중"), "open overtime session enables overtime check-out only");
        Invoke(window, "UpdateAttendanceUI", new AttendanceRecord { CheckedIn = true, CheckedOut = true, BlockReason = "휴직 중", OvertimeReason = "퇴근 후 재출근" });
        Check(((FrameworkElement)window.FindName("attendanceDonePanel")).Visibility == Visibility.Visible && (string)((Button)window.FindName("btnCheckIn")).Content == "출근", "blocked day never offers overtime check-in");
        Check(((TextBlock)window.FindName("txtTodayDate")).Text.StartsWith(DateTime.Today.ToString("yyyy.MM.dd")), "date label updates with clock");
        Console.WriteLine("PASS: actual timer rollover and external attendance refresh");
        windowApi.Dispose();
        Set(window, "_isExplicitExit", true);
        window.Close();
    }

    private static void VerifyRenderingPreferences()
    {
        string path = Path.Combine(Path.GetTempPath(), $"worksystem-rendering-{Guid.NewGuid():N}.json");
        AppSettingsService IsolatedService()
        {
            var service = new AppSettingsService();
            Set(service, "_settingsPath", path);
            return service;
        }
        var method = typeof(AppSettingsService).Assembly.GetType("Portfolio.OfficeSystem.RenderingPreferences")!
            .GetMethod("ResolveMode", BindingFlags.Static | BindingFlags.NonPublic)!;
        RenderMode Resolve(AppSettingsService service, params string[] args) => (RenderMode)method.Invoke(null, new object[] { args, service })!;
        try
        {
            var settingsService = IsolatedService();
            Check(Resolve(settingsService) == RenderMode.Default && !File.Exists(path), "fresh profiles keep the default rendering path without a settings write");
            Check(Resolve(settingsService, "--software-rendering") == RenderMode.SoftwareOnly, "temporary software flag applies to this launch");
            Check(Resolve(settingsService) == RenderMode.Default && !File.Exists(path), "temporary software flag does not persist");

            settingsService.Save(new DesktopAppSettings
            {
                ServerUrl = "http://localhost:18080", Username = "rendering-regression", ThemeMode = "Dark",
                DeviceToken = "rendering-regression-token", MainWindowLeft = 30, MainWindowTop = 40,
                MainWindowWidth = 520, MainWindowHeight = 720
            });
            Check(Resolve(settingsService, "--save-rendering-mode=software") == RenderMode.SoftwareOnly, "explicit software preference applies immediately");
            var afterSave = IsolatedService();
            Check(Resolve(afterSave) == RenderMode.SoftwareOnly, "ordinary launch in a new service uses the saved preference");
            var settings = afterSave.Load();
            Check(settings.UseSoftwareRendering && settings.Username == "rendering-regression" && settings.ThemeMode == "Dark"
                && settings.DeviceToken == "rendering-regression-token" && settings.MainWindowLeft == 30,
                "saving rendering mode preserves existing profile settings and credentials");
            Check(!File.ReadAllText(path).Contains("rendering-regression-token"), "preference persistence keeps the token encrypted");
            settings.ThemeMode = "Light";
            settings.MainWindowWidth = 560;
            afterSave.Save(settings);
            afterSave.ClearToken();
            Check(Resolve(IsolatedService()) == RenderMode.SoftwareOnly && afterSave.Load().DeviceToken == null,
                "theme, placement and token updates retain the rendering preference");

            settings = afterSave.Load();
            settings.EncryptedToken = "invalid-base64!";
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(settings));
            var recovered = IsolatedService().Load();
            Check(recovered.UseSoftwareRendering && recovered.Username == "rendering-regression" && recovered.MainWindowWidth == 560
                && recovered.DeviceToken == null && recovered.EncryptedToken == null,
                "an unreadable token clears only authentication and retains the compatibility preference");
            Check(Resolve(IsolatedService()) == RenderMode.SoftwareOnly, "ordinary launch retains software rendering after token corruption");
            Check(Resolve(IsolatedService(), "--save-rendering-mode=default") == RenderMode.Default
                && Resolve(IsolatedService()) == RenderMode.Default && !IsolatedService().Load().UseSoftwareRendering,
                "explicit default preference restores ordinary rendering on later launches");
            Console.WriteLine("PASS: isolated rendering preference persistence, transient override, profile preservation, token recovery and reset");
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class SettingsIsolation : IDisposable
    {
        private readonly MainWindow _window;
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"worksystem-regression-{Guid.NewGuid():N}.json");

        public SettingsIsolation(MainWindow window)
        {
            _window = window;
            Set(Field<AppSettingsService>(window, "_settingsService"), "_settingsPath", _path);
        }

        public void Dispose()
        {
            // 예외가 발생해도 창 종료 저장은 임시 경로에만 수행한다.
            Field<DispatcherTimer>(_window, "_timer").Stop();
            Field<DispatcherTimer>(_window, "_refreshTimer").Stop();
            Set(_window, "_isExplicitExit", true);
            _window.Close();
            Field<DispatcherTimer?>(_window, "_posSaveTimer")?.Stop();
            File.Delete(_path);
            File.Delete(_path + ".tmp");
        }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static async Task UntilAsync(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ready()) { if (DateTime.UtcNow >= until) throw new TimeoutException("Expected update did not arrive"); await Task.Delay(30); }
    }

    private sealed class DesktopServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<WebSocket> _sockets = new();
        private readonly TaskCompletionSource<WebSocket> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _otp;
        private bool _password;
        public ConcurrentQueue<string> Paths { get; } = new();
        public ConcurrentQueue<string> RevokedTokens { get; } = new();
        public string Url { get; }
        public bool CheckedIn { get; set; }
        /// <summary>true 면 보호 API 를 웹 로그인 화면(/login)으로 302 — 서버 재시작으로 세션이 끊긴 상태.</summary>
        public bool SessionExpired { get; set; }

        public DesktopServer()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Url = $"http://localhost:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try { while (!_stop.IsCancellationRequested) { var request = await _listener.GetContextAsync(); _ = HandleAsync(request); } }
                catch (Exception) when (_stop.IsCancellationRequested) { }
            });
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            string path = context.Request.Url!.AbsolutePath;
            Paths.Enqueue(path);
            if (context.Request.IsWebSocketRequest)
            {
                var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                _sockets.Add(ws);
                _first.TrySetResult(ws);
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream);
            string body = await reader.ReadToEndAsync();
            string response = "{}";
            if (SessionExpired && path.StartsWith("/api/desktop/attendance/"))
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = Url + "/login";
                context.Response.Close();
                return;
            }
            if (path == "/login")
            {
                byte[] html = Encoding.UTF8.GetBytes("<html><body>login</body></html>");
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(html);
                context.Response.Close();
                return;
            }
            if (path is "/api/desktop/login" or "/api/desktop/refresh") response = "{\"authenticated\":true,\"token\":\"test-token\",\"user\":{\"userId\":42,\"mustChangePassword\":true}}";
            else if (path == "/api/desktop/logout")
            {
                using var document = System.Text.Json.JsonDocument.Parse(body);
                RevokedTokens.Enqueue(document.RootElement.GetProperty("token").GetString()!);
            }
            else if (path == "/api/desktop/otp/state") response = "{\"required\":true,\"enrolled\":true}";
            else if (path == "/api/desktop/otp/verify") { _otp = true; response = "{\"ok\":true}"; }
            else if (path == "/api/desktop/password")
            {
                _password = _otp && body.Contains("initial-password");
                response = _password ? "{\"ok\":true}" : "{\"ok\":false,\"message\":\"현재 비밀번호가 올바르지 않습니다.\"}";
                if (!_password) context.Response.StatusCode = 400;
            }
            else if (path == "/api/right-panel")
            {
                if (!_otp || !_password) context.Response.StatusCode = 401;
                response = "{\"notifications\":[]}";
            }
            else if (path == "/api/desktop/attendance/today") response = CheckedIn ? "{\"checkedIn\":true,\"checkInAt\":\"2026-10-16T09:00:00\"}" : "{\"checkedIn\":false,\"unrequestedOvertimeCount\":2}";
            else if (path == "/api/desktop/attendance/preview") response = "{\"allowed\":true,\"label\":\"지각\",\"nowTime\":\"09:12:00\"}";
            byte[] bytes = Encoding.UTF8.GetBytes(response);
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        public async Task DisconnectFirstAsync() => await (await _first.Task).CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect regression", CancellationToken.None);
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            foreach (var ws in _sockets) ws.Dispose();
            _stop.Dispose();
        }
    }
}
