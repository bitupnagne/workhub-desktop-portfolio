using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Portfolio.OfficeSystem;

public partial class App : Application
{
    private const string AppUserModelId = "Portfolio.OfficeSystem.Desktop";
    private const string SingleInstanceMutexName = "Global\\Portfolio.OfficeSystem.SingleInstance";
    private static Mutex? _singleInstanceMutex;
    private SplashWindow? _splashWindow;
    private Thread? _splashThread;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 스플래시를 포함한 첫 WPF 창이 만들어지기 전에 이 앱의 렌더링 모드를 결정한다.
        System.Windows.Media.RenderOptions.ProcessRenderMode = RenderingPreferences.ResolveMode(e.Args, new AppSettingsService());
        StartupDiagnostics.Start(e.Args);
        StartupDiagnostics.Write("App.OnStartup.enter");
        base.OnStartup(e);

        // [1.0.88] 전역 예외 핸들러 — quota 부족(1816) 등으로 unhandled 가 나도 프로세스 죽지 않게.
        DispatcherUnhandledException += (s, ex) =>
        {
            System.Diagnostics.Debug.WriteLine($"[Dispatcher] {ex.Exception}");
            try { LogUnhandled("Dispatcher", ex.Exception); } catch { }
            try
            {
                MessageBox.Show(
                    "예기치 못한 오류가 발생했습니다. 작업을 다시 시도해 주세요.\n\n" + ex.Exception.Message,
                    "WorkHub Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            try { LogUnhandled("AppDomain", ex.ExceptionObject as Exception); } catch { }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            try { LogUnhandled("Task", ex.Exception); } catch { }
            ex.SetObserved();
        };

        // Single-instance guard: if an instance already exists, bring it to front and exit.
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                // [W4] 창 제목("WorkHub Desktop") 의존 제거 — 브랜딩으로 제목이 바뀌어도
                // 같은 프로세스명을 찾아 MainWindowHandle로 포커스. 실패 시 종전 제목 검색 폴백.
                var hWnd = IntPtr.Zero;
                var current = System.Diagnostics.Process.GetCurrentProcess();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id == current.Id) continue;
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        hWnd = p.MainWindowHandle;
                        break;
                    }
                }
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = FindWindowW(null, "WorkHub Desktop");
                }
                if (hWnd != IntPtr.Zero)
                {
                    ShowWindow(hWnd, 9); // SW_RESTORE
                    SetForegroundWindow(hWnd);
                }
            }
            catch { }
            Shutdown();
            return;
        }

        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

        // SplashWindow on dedicated STA thread — centered, with animated progress bar.
        // Started BEFORE heavy resource merge so user sees splash immediately.
        var splashReady = new ManualResetEventSlim(false);
        _splashThread = new Thread(() =>
        {
            StartupDiagnostics.Write("splash.create.begin");
            _splashWindow = new SplashWindow();
            StartupDiagnostics.Attach(_splashWindow, "splash");
            _splashWindow.Show();
            StartupDiagnostics.Snapshot(_splashWindow, "splash.Show.returned");
            splashReady.Set();
            Dispatcher.Run();
        });
        _splashThread.SetApartmentState(ApartmentState.STA);
        _splashThread.IsBackground = true;
        _splashThread.Start();
        splashReady.Wait(TimeSpan.FromMilliseconds(500));

        StartupDiagnostics.Write("app.resources.begin");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/Tokens.xaml", UriKind.Relative) });
        var settings = new AppSettingsService().Load();
        var mode = string.Equals(settings.ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase) ? Portfolio.OfficeSystem.ThemeMode.Dark : Portfolio.OfficeSystem.ThemeMode.Light;
        ThemeService.Apply(mode);
        StartupDiagnostics.Write("app.resources.complete");

        InitializeMainApp();
        StartupDiagnostics.Write("App.OnStartup.complete");
    }

    private static void LogUnhandled(string source, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Portfolio.OfficeSystem", "logs");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var line = $"[{DateTime.Now:HH:mm:ss}] [{source}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n\n";
            System.IO.File.AppendAllText(path, line);
        }
        catch { }
    }

    private void InitializeMainApp()
    {
        StartupDiagnostics.Write("main.create.begin");
        var mainWindow = new MainWindow();
        StartupDiagnostics.Attach(mainWindow, "main");
        mainWindow.StartupStatusChanged = message =>
        {
            _splashWindow?.Dispatcher.BeginInvoke(new Action(() => _splashWindow?.SetStatus(message)));
        };
        mainWindow.StartupThemeChanged = theme =>
        {
            _splashWindow?.Dispatcher.BeginInvoke(new Action(() => _splashWindow?.ApplyTheme(theme)));
        };
        // [E-13] 준비되기 전(업데이트 확인 등)에는 화면 밖에 두어 '검은 창'이 보이지 않게 한다.
        mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        mainWindow.Left = -32000; mainWindow.Top = -32000;
        mainWindow.ReadyToShow += () =>
        {
            StartupDiagnostics.Snapshot(mainWindow, "main.ReadyToShow");
            // 준비 완료 → 화면 중앙으로 이동 후 표시
            var wa = SystemParameters.WorkArea;
            mainWindow.Left = wa.Left + System.Math.Max(0, (wa.Width - mainWindow.Width) / 2);
            mainWindow.Top = wa.Top + System.Math.Max(0, (wa.Height - mainWindow.Height) / 2);
            mainWindow.Visibility = Visibility.Visible;
            mainWindow.Activate();

            // 2) Splash 페이드 아웃 후 close (250ms)
            _splashWindow?.Dispatcher.BeginInvoke(new Action(() =>
            {
                var splash = _splashWindow;
                if (splash == null) return;
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(splash.Opacity, 0, TimeSpan.FromMilliseconds(250));
                fadeOut.Completed += (_, __) =>
                {
                    splash.Close();
                    splash.Dispatcher.InvokeShutdown();
                };
                splash.BeginAnimation(Window.OpacityProperty, fadeOut);
            }));
        };
        mainWindow.Show();
        StartupDiagnostics.Snapshot(mainWindow, "main.Show.returned");
    }
}
