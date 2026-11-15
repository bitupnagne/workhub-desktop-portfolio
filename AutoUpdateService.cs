using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Portfolio.OfficeSystem;

public class AutoUpdateService
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string CurrentVersion => NormalizeCurrentVersion(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "1.0.0");

    /// <summary>[W5] 5초 타임아웃 1회 시도는 느린 망에서 조용히 스킵돼 mandatory도 구버전으로
    /// 계속 사용 가능했음 — 짧은 간격 재시도(기본 3회)로 견고화.</summary>
    public async Task<AppUpdateInfo?> CheckAsync(string serverUrl, int attempts = 3)
    {
        for (int i = 1; i <= attempts; i++)
        {
            try
            {
                return await CheckOnceAsync(serverUrl);
            }
            catch when (i < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        return null;
    }

    private async Task<AppUpdateInfo?> CheckOnceAsync(string serverUrl)
    {
        string baseUrl = NormalizeServerUrl(serverUrl);
        string url = $"{baseUrl}/api/desktop/releases/latest?currentVersion={Uri.EscapeDataString(CurrentVersion)}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _httpClient.GetAsync(url, cts.Token);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var info = JsonSerializer.Deserialize<AppUpdateInfo>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return info?.UpdateAvailable == true ? info : null;
    }

    public async Task<AppReleaseNoteInfo?> GetReleaseNoteAsync(string serverUrl)
    {
        string baseUrl = NormalizeServerUrl(serverUrl);
        string url = $"{baseUrl}/api/desktop/releases/{Uri.EscapeDataString(CurrentVersion)}/note";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _httpClient.GetAsync(url, cts.Token);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AppReleaseNoteInfo>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public async Task<string> DownloadAndVerifyAsync(AppUpdateInfo update, IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            throw new InvalidOperationException("다운로드 주소가 없습니다.");

        string updatesDir = GetUpdatesDirectory();
        Directory.CreateDirectory(updatesDir);

        string extension = string.Equals(update.PackageType, "PATCH", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".exe";
        string fileName = string.Equals(update.PackageType, "PATCH", StringComparison.OrdinalIgnoreCase)
            ? $"Portfolio.OfficeSystem-Patch-{update.Version}{extension}"
            : $"Portfolio.OfficeSystem-Setup-{update.Version}{extension}";
        string target = Path.Combine(updatesDir, fileName);
        string temp = target + ".download";
        if (File.Exists(temp)) File.Delete(temp);

        using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? update.FileSize ?? 0;

        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = File.Create(temp))
        {
            var buffer = new byte[128 * 1024];
            long readTotal = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer);
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read));
                readTotal += read;
                if (total > 0) progress?.Report(readTotal * 100.0 / total);
            }
        }

        if (update.FileSize.HasValue && new FileInfo(temp).Length != update.FileSize.Value)
        {
            File.Delete(temp);
            throw new InvalidOperationException("다운로드한 파일 크기가 올바르지 않습니다.");
        }

        string actualHash = await Sha256Async(temp);
        if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidOperationException("다운로드한 파일 검증에 실패했습니다.");
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(temp, target);
        RemoveZoneIdentifier(target);
        return target;
    }

    public void StartInstaller(string packagePath, string packageType, WindowBounds? ownerBounds = null)
    {
        string appPath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Portfolio.OfficeSystem.exe");
        string updaterPath = Path.Combine(AppContext.BaseDirectory, "Portfolio.OfficeSystem.Updater.exe");
        if (File.Exists(updaterPath))
        {
            bool isPatch = string.Equals(packageType, "PATCH", StringComparison.OrdinalIgnoreCase);
            bool updatesUpdater = isPatch && IsUpdaterSelfUpdatePackage(packagePath);

            // [중요] FULL 설치(Inno)는 {app} 폴더를 통째로 덮어쓰며 CloseApplications=force 로
            // {app} 안에서 실행 중인 프로세스를 강제 종료한다. 업데이터가 {app}에서 돌면 설치 도중
            // 강제 종료돼 WaitForExit 가 끊기고, 설치 후 앱 재시작(StartApp)도 일어나지 않아
            // "업데이트 완료/기존 설치파일 실행 중"에서 멈춘 채 무반응이 된다.
            // → 업데이터를 {app} 밖(임시 폴더)에서 실행해 설치기와 충돌하지 않게 한다.
            //   (업데이터는 self-contained single-file exe 라 exe 하나만 복사하면 독립 실행됨)
            string runUpdaterPath = updaterPath;
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "PortfolioOfficeUpdater-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string tempUpdater = Path.Combine(tempDir, "Portfolio.OfficeSystem.Updater.exe");
                File.Copy(updaterPath, tempUpdater, true);
                runUpdaterPath = tempUpdater;
            }
            catch
            {
                // 임시 복사 실패 시 최소한 기존 위치에서 실행(PATCH 는 그대로 동작).
                runUpdaterPath = updaterPath;
            }

            var updaterStartInfo = new ProcessStartInfo
            {
                FileName = runUpdaterPath,
                Arguments = $"--package=\"{packagePath}\" --packageType=\"{packageType}\" --app=\"{appPath}\""
                    + FormatOwnerBounds(ownerBounds)
                    + (isPatch && !updatesUpdater ? " --silent" : ""),
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(updaterStartInfo);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = packagePath,
            Arguments = "/AUTOUPDATE /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS",
            UseShellExecute = true
        };
        Process.Start(startInfo);
    }

    private static bool IsUpdaterSelfUpdatePackage(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            return archive.Entries.Any(entry =>
                Path.GetFileName(entry.FullName).Equals("Portfolio.OfficeSystem.Updater.pending.exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(entry.FullName).Equals("Portfolio.OfficeSystem.Updater.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string FormatOwnerBounds(WindowBounds? bounds)
    {
        if (bounds == null)
            return "";

        return FormattableString.Invariant(
            $" --ownerLeft={bounds.Left} --ownerTop={bounds.Top} --ownerWidth={bounds.Width} --ownerHeight={bounds.Height}");
    }

    private static string GetUpdatesDirectory()
    {
        string commonUpdatesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Portfolio.OfficeSystem",
            "Updates");

        try
        {
            Directory.CreateDirectory(commonUpdatesDir);
            string probe = Path.Combine(commonUpdatesDir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return commonUpdatesDir;
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Portfolio.OfficeSystem",
                "Updates");
        }
    }

    private static void RemoveZoneIdentifier(string path)
    {
        try
        {
            string zoneIdentifier = path + ":Zone.Identifier";
            if (File.Exists(zoneIdentifier))
                File.Delete(zoneIdentifier);
        }
        catch
        {
        }
    }

    private static string NormalizeServerUrl(string serverUrl)
    {
        return string.IsNullOrWhiteSpace(serverUrl) ? "http://localhost:18080" : serverUrl.Trim().TrimEnd('/');
    }

    private static string NormalizeCurrentVersion(string version)
    {
        var normalized = version.Trim();
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
            normalized = normalized[..plusIndex];
        return string.IsNullOrWhiteSpace(normalized) ? "1.0.0" : normalized;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public record WindowBounds(double Left, double Top, double Width, double Height);

public class AppUpdateInfo
{
    public bool UpdateAvailable { get; set; }
    public string? Version { get; set; }
    public bool Mandatory { get; set; }
    public string? PackageType { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public long? FileSize { get; set; }
    public string? ReleaseNote { get; set; }
}

public class AppReleaseNoteInfo
{
    public string? Version { get; set; }
    public string? ReleaseNote { get; set; }
}
