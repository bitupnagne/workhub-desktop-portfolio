using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Portfolio.OfficeSystem.Updater;

public partial class MainWindow : Window
{
    private readonly string? _packagePath;
    private readonly string? _appPath;
    private readonly string _packageType;
    private readonly bool _silentMode;
    private readonly OwnerBounds? _ownerBounds;

    public bool IsSilentMode => _silentMode;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        (_packagePath, _packageType, _appPath, _silentMode, _ownerBounds) = ParseArgs(args);
        ApplyStartupPosition();
        ApplyBranding();
        Loaded += MainWindow_Loaded;
    }

    // 메인 앱이 캐시해 둔 로고(PNG)와 설치 대상 버전(패키지 파일명에서 파싱)을 표시. 실패해도 무시.
    private void ApplyBranding()
    {
        try
        {
            var png = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Portfolio.OfficeSystem", "portfolio-branding", "desktop-logo-latest.png");
            if (File.Exists(png) && new FileInfo(png).Length > 0)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(png, UriKind.Absolute);
                bmp.EndInit();
                UpdaterLogo.Source = bmp;
                UpdaterLogo.Visibility = Visibility.Visible;
            }
        }
        catch { }

        try
        {
            var version = ExtractVersion(_packagePath);
            if (!string.IsNullOrEmpty(version))
                VersionText.Text = $"v{version} 적용 중";
        }
        catch { }
    }

    // "Portfolio.OfficeSystem-Setup-1.0.11.exe" / "...-Patch-1.0.11.zip" 등에서 버전 추출.
    private static string? ExtractVersion(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(packagePath), @"(\d+\.\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunUpdateAsync();
    }

    private void ApplyStartupPosition()
    {
        if (_ownerBounds == null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Left = _ownerBounds.Left + Math.Max(0, (_ownerBounds.Width - Width) / 2);
        Top = _ownerBounds.Top + Math.Max(0, (_ownerBounds.Height - Height) / 2);

        // [P2] 보조 모니터의 앱 위에서 실행돼도 리셋되지 않도록 전체 가상 데스크톱 기준으로 판정
        double vsLeft = SystemParameters.VirtualScreenLeft;
        double vsTop = SystemParameters.VirtualScreenTop;
        double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
        if (Left + Width < vsLeft + 40 || Left > vsRight - 40 || Top + Height < vsTop + 40 || Top > vsBottom - 40)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        }
    }

    public async Task RunUpdateAsync()
    {
        if (!_silentMode)
            StartProgressAnimation();

        try
        {
            if (string.IsNullOrWhiteSpace(_packagePath) || !File.Exists(_packagePath))
                throw new InvalidOperationException("업데이트 패키지 파일을 찾을 수 없습니다.");

            await Task.Run(RunUpdate);
            SetStatus("업데이트가 완료되었습니다. 앱을 다시 시작합니다...");
            StartApp();
            await Task.Delay(1000);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus("업데이트 중 오류가 발생했습니다.");
            MessageBox.Show(
                "업데이트를 완료하지 못했습니다.\n\n" + ex.Message,
                "업데이트 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Application.Current.Shutdown(1);
        }
    }

    private void RunUpdate()
    {
        SetStatus("실행 중인 앱을 종료하고 있습니다...");
        KillProcess("Portfolio.OfficeSystem");
        Task.Delay(1200).Wait();

        if (string.Equals(_packageType, "PATCH", StringComparison.OrdinalIgnoreCase))
        {
            ApplyPatchPackage();
            return;
        }

        SetStatus("기존 설치파일을 실행하고 있습니다...");
        var startInfo = new ProcessStartInfo
        {
            FileName = _packagePath!,
            Arguments = "/AUTOUPDATE /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS",
            UseShellExecute = true,
            Verb = "runas"
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("설치 프로그램을 시작하지 못했습니다.");
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"설치 프로그램이 실패했습니다. ExitCode={process.ExitCode}");
    }

    private void ApplyPatchPackage()
    {
        string appPath = GetAppPath();
        string appDir = Path.GetDirectoryName(appPath)
            ?? throw new InvalidOperationException("앱 설치 경로를 찾을 수 없습니다.");
        string extractDir = Path.Combine(Path.GetTempPath(), "Portfolio.OfficeSystem-Patch-" + Guid.NewGuid().ToString("N"));
        // [U1] 덮어쓰기 전 원본 백업 — 도중 실패(파일 잠김·백신 간섭) 시 복원해 반쯤 패치된 상태를 막는다.
        string backupDir = Path.Combine(Path.GetTempPath(), "Portfolio.OfficeSystem-Backup-" + Guid.NewGuid().ToString("N"));
        var replaced = new List<(string Target, string Backup)>();
        var created = new List<string>();
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(backupDir);

        try
        {
            SetStatus("패치를 풀고 있습니다...");
            ZipFile.ExtractToDirectory(_packagePath!, extractDir, true);

            SetStatus("패치를 적용하고 있습니다...");
            var sources = Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories)
                .Where(s => !Path.GetFileName(s).Equals("removed.txt", StringComparison.OrdinalIgnoreCase))
                .Where(s => !IsUpdaterFile(Path.GetRelativePath(extractDir, s)))
                .ToList();
            int total = sources.Count;
            int done = 0;
            foreach (var source in sources)
            {
                string relative = Path.GetRelativePath(extractDir, source);
                string target = Path.Combine(appDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    string backup = Path.Combine(backupDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                    replaced.Add((target, backup));
                }
                else
                {
                    created.Add(target);
                }
                File.Copy(source, target, true);
                done++;
                SetProgress(total > 0 ? done * 100.0 / total : 100);
            }
            ApplyRemovals(extractDir, appDir, backupDir, replaced);
            SetProgress(100);
        }
        catch
        {
            SetStatus("패치 적용에 실패하여 이전 상태로 복원하고 있습니다...");
            RollbackPatch(replaced, created);
            throw;
        }
        finally
        {
            try { Directory.Delete(extractDir, true); } catch { }
            try { Directory.Delete(backupDir, true); } catch { }
        }
    }

    /** [U1] 백업본으로 원복 — 교체분은 되돌리고, 새로 생긴 파일은 삭제. */
    private static void RollbackPatch(List<(string Target, string Backup)> replaced, List<string> created)
    {
        foreach (var (target, backup) in replaced)
        {
            try
            {
                if (File.Exists(backup))
                    File.Copy(backup, target, true);
            }
            catch { }
        }
        foreach (var target in created)
        {
            try
            {
                if (File.Exists(target))
                    File.Delete(target);
            }
            catch { }
        }
    }

    private static bool IsUpdaterFile(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        return fileName.Equals("Portfolio.OfficeSystem.Updater.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Portfolio.OfficeSystem.Updater.pdb", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyRemovals(string extractDir, string appDir,
                                      string backupDir, List<(string Target, string Backup)> replaced)
    {
        string removedPath = Path.Combine(extractDir, "removed.txt");
        if (!File.Exists(removedPath))
            return;

        // [U2] 상대경로가 앱 폴더 밖("..\.." 류)을 가리키면 무시 — 방어적 가드.
        string appRoot = Path.GetFullPath(appDir + Path.DirectorySeparatorChar);
        foreach (var line in File.ReadAllLines(removedPath))
        {
            string relative = line.Trim();
            if (string.IsNullOrWhiteSpace(relative))
                continue;
            string target = Path.GetFullPath(Path.Combine(appDir, relative));
            if (!target.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (File.Exists(target))
                {
                    // [U1] 삭제 전 백업 — 실패 복원 시 함께 되살린다.
                    string backup = Path.Combine(backupDir, "removed", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                    replaced.Add((target, backup));
                    File.Delete(target);
                }
            }
            catch
            {
            }
        }
    }

    private string GetAppPath()
    {
        if (!string.IsNullOrWhiteSpace(_appPath) && File.Exists(_appPath))
            return _appPath;

        string registryPath = GetInstalledAppPathFromRegistry();
        if (File.Exists(registryPath))
            return registryPath;

        string programFilesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Portfolio.OfficeSystem",
            "Portfolio.OfficeSystem.exe");
        if (File.Exists(programFilesPath))
            return programFilesPath;

        return Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Portfolio.OfficeSystem.exe");
    }

    private void StartApp()
    {
        string appPath = WaitForAppPath();
        if (string.IsNullOrWhiteSpace(appPath))
            throw new InvalidOperationException("설치 완료 후 앱 실행 파일을 찾을 수 없습니다.");

        // [U3] updater는 관리자 권한(runas)으로 실행되므로 Process.Start 직행이면 앱이
        // elevated 토큰을 상속 — 드래그앤드롭 차단 등 미묘한 부작용. explorer.exe(셸) 경유로
        // 일반 권한 재시작, 실패 시 종전 방식 폴백.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = "\"" + appPath + "\"",
                UseShellExecute = true
            });
            return;
        }
        catch
        {
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = true
        });
    }

    private string WaitForAppPath()
    {
        for (int i = 0; i < 60; i++)
        {
            string appPath = GetAppPath();
            if (File.Exists(appPath))
                return appPath;
            Task.Delay(500).Wait();
        }

        return "";
    }

    private static string GetInstalledAppPathFromRegistry()
    {
        const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Portfolio.OfficeSystem_is1";
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(uninstallKey);
            string? uninstallString = key?.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(uninstallString))
                continue;

            string uninstallerPath = uninstallString.Trim().Trim('"');
            string? installDir = Path.GetDirectoryName(uninstallerPath);
            if (!string.IsNullOrWhiteSpace(installDir))
                return Path.Combine(installDir, "Portfolio.OfficeSystem.exe");
        }

        return "";
    }

    private void SetStatus(string message)
    {
        if (_silentMode)
            return;

        Dispatcher.Invoke(() => StatusText.Text = message);
    }

    // 패치 적용 단계 진행률(%) — 결정형 바 + 퍼센트 텍스트로 전환.
    private void SetProgress(double pct)
    {
        if (_silentMode)
            return;

        Dispatcher.Invoke(() =>
        {
            AnimationBar.Visibility = Visibility.Collapsed;
            DetermFill.Visibility = Visibility.Visible;
            DetermFill.Width = Math.Max(0, Math.Min(320, 320 * pct / 100.0));
            PercentText.Visibility = Visibility.Visible;
            PercentText.Text = $"{pct:0}%";
        });
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void StartProgressAnimation()
    {
        var animation = new DoubleAnimation
        {
            From = -120,
            To = 320,
            Duration = TimeSpan.FromSeconds(1.4),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        BarTranslate.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private static void KillProcess(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch
            {
            }
        }
    }

    private static (string? PackagePath, string PackageType, string? AppPath, bool SilentMode, OwnerBounds? OwnerBounds) ParseArgs(IEnumerable<string> args)
    {
        string? packagePath = null;
        string? appPath = null;
        string packageType = "FULL";
        bool silentMode = false;
        double? ownerLeft = null;
        double? ownerTop = null;
        double? ownerWidth = null;
        double? ownerHeight = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--package=", StringComparison.OrdinalIgnoreCase))
                packagePath = Unquote(arg["--package=".Length..]);
            else if (arg.StartsWith("--setup=", StringComparison.OrdinalIgnoreCase))
                packagePath = Unquote(arg["--setup=".Length..]);
            else if (arg.StartsWith("--app=", StringComparison.OrdinalIgnoreCase))
                appPath = Unquote(arg["--app=".Length..]);
            else if (arg.StartsWith("--packageType=", StringComparison.OrdinalIgnoreCase))
                packageType = Unquote(arg["--packageType=".Length..]).ToUpperInvariant();
            else if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase))
                silentMode = true;
            else if (arg.StartsWith("--ownerLeft=", StringComparison.OrdinalIgnoreCase))
                ownerLeft = ParseDouble(arg["--ownerLeft=".Length..]);
            else if (arg.StartsWith("--ownerTop=", StringComparison.OrdinalIgnoreCase))
                ownerTop = ParseDouble(arg["--ownerTop=".Length..]);
            else if (arg.StartsWith("--ownerWidth=", StringComparison.OrdinalIgnoreCase))
                ownerWidth = ParseDouble(arg["--ownerWidth=".Length..]);
            else if (arg.StartsWith("--ownerHeight=", StringComparison.OrdinalIgnoreCase))
                ownerHeight = ParseDouble(arg["--ownerHeight=".Length..]);
        }

        silentMode = silentMode && string.Equals(packageType, "PATCH", StringComparison.OrdinalIgnoreCase);
        OwnerBounds? ownerBounds = ownerLeft.HasValue && ownerTop.HasValue && ownerWidth.HasValue && ownerHeight.HasValue
            ? new OwnerBounds(ownerLeft.Value, ownerTop.Value, ownerWidth.Value, ownerHeight.Value)
            : null;
        return (packagePath, packageType, appPath, silentMode, ownerBounds);
    }

    private static string Unquote(string value)
    {
        return value.Trim().Trim('"');
    }

    private static double? ParseDouble(string value)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private record OwnerBounds(double Left, double Top, double Width, double Height);
}
