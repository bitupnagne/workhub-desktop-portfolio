using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Portfolio.OfficeSystem;

// 명시적으로 요청한 시작 진단만 로컬 로그에 남긴다. 계정·토큰·서버 응답은 기록하지 않는다.
internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Elapsed = new();
    private static string? _path;
    private static int _lines;

    internal static void Start(string[] arguments)
    {
        bool trace = arguments.Contains("--startup-trace", StringComparer.OrdinalIgnoreCase);
        if (!trace) return;
        try
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            _path = Path.Combine(AppLog.LogDirectory, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            Elapsed.Start();
            Write("process", $"runtime={Environment.Version}; renderMode={RenderOptions.ProcessRenderMode}; tier={RenderCapability.Tier >> 16}");
            foreach (var source in new[] { PresentationTraceSources.MarkupSource, PresentationTraceSources.ResourceDictionarySource, PresentationTraceSources.AnimationSource })
            {
                source.Listeners.Add(new StartupTraceListener());
                source.Switch.Level = SourceLevels.Warning;
            }
        }
        catch { _path = null; }
    }

    internal static void Write(string stage, string detail = "")
    {
        if (_path == null || Elapsed.Elapsed > TimeSpan.FromSeconds(30)) return;
        try
        {
            lock (Gate)
            {
                if (++_lines > 500) return;
                File.AppendAllText(_path, $"[{Elapsed.Elapsed.TotalMilliseconds:F1}ms] [thread {Environment.CurrentManagedThreadId}] {stage} {detail}{Environment.NewLine}");
            }
        }
        catch { }
    }

    internal static void Attach(Window window, string name)
    {
        if (_path == null) return;
        Snapshot(window, name + ".constructed");
        window.SourceInitialized += (_, _) => Snapshot(window, name + ".SourceInitialized");
        window.Loaded += (_, _) => Snapshot(window, name + ".Loaded");
        window.ContentRendered += (_, _) => Snapshot(window, name + ".ContentRendered");
        window.IsVisibleChanged += (_, _) => Snapshot(window, name + ".VisibilityChanged");
        window.Closed += (_, _) => Write(name + ".Closed");
        window.Dispatcher.UnhandledException += (_, error) => Write(name + ".dispatcher-error", error.Exception.ToString());
        EventHandler? firstRender = null;
        firstRender = (_, _) =>
        {
            if (!window.IsVisible || !window.IsLoaded || PresentationSource.FromVisual(window) == null) return;
            CompositionTarget.Rendering -= firstRender;
            Snapshot(window, name + ".Rendering");
        };
        CompositionTarget.Rendering += firstRender;
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); Snapshot(window, name + ".after-5s"); };
        window.Closed += (_, _) => { timer.Stop(); CompositionTarget.Rendering -= firstRender; };
        timer.Start();
    }

    internal static void Snapshot(Window window, string stage)
    {
        if (_path == null) return;
        try
        {
            string view(FrameworkElement element) => string.Create(CultureInfo.InvariantCulture,
                $"{element.Name}:{element.Visibility}/visible={element.IsVisible}/opacity={element.Opacity}/actual={element.ActualWidth}x{element.ActualHeight}/render={element.RenderSize}/measure={element.IsMeasureValid}/arrange={element.IsArrangeValid}");
            var root = window.Content as FrameworkElement;
            var source = PresentationSource.FromVisual(window) as HwndSource;
            var target = source?.CompositionTarget;
            string background = window.Background is SolidColorBrush brush ? brush.Color.ToString() : window.Background?.GetType().Name ?? "null";
            var app = Application.Current;
            string appMain = app == null ? "null"
                : app.Dispatcher.CheckAccess() ? app.MainWindow?.GetType().Name ?? "null"
                : "owned-by-app-dispatcher";
            Write(stage, string.Create(CultureInfo.InvariantCulture,
                $"{view(window)}; position={window.Left},{window.Top}; state={window.WindowState}; loaded={window.IsLoaded}; active={window.IsActive}; background={background}; transparent={window.AllowsTransparency}; appMain={appMain}; source={source != null}; targetMode={target?.RenderMode}; targetBackground={target?.BackgroundColor}; dpi={VisualTreeHelper.GetDpi(window).DpiScaleX},{VisualTreeHelper.GetDpi(window).DpiScaleY}; root={(root == null ? "null" : view(root))}"));
            foreach (string childName in new[] { "initialLoadingView", "loginView", "mainView" })
                if (window.FindName(childName) is FrameworkElement child) Write(stage + ".view", view(child));
        }
        catch (Exception error) { Write(stage + ".snapshot-error", error.GetType().Name + ": " + error.Message); }
    }

    private sealed class StartupTraceListener : TraceListener
    {
        public override void Write(string? message) => StartupDiagnostics.Write("wpf-warning", message ?? "");
        public override void WriteLine(string? message) => StartupDiagnostics.Write("wpf-warning", message ?? "");
    }
}
