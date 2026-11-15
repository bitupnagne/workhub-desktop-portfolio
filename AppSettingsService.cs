using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Portfolio.OfficeSystem;

public class AppSettingsService
{
    // [P1] 서비스 인스턴스가 여러 곳(App/MainWindow 등)에서 생성되므로 프로세스 전역 락으로
    // Load/Save를 직렬화 — 메인창·채팅창 동시 저장의 read-modify-write 레이스로 저장분이 유실되던 문제 방지.
    private static readonly object IoLock = new();
    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Portfolio.OfficeSystem");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
    }

    public DesktopAppSettings Load()
    {
        lock (IoLock)
        {
            try
            {
                if (!File.Exists(_settingsPath)) return new DesktopAppSettings();
                var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                var settings = JsonSerializer.Deserialize<DesktopAppSettings>(json) ?? new DesktopAppSettings();
                if (!string.IsNullOrEmpty(settings.EncryptedToken))
                {
                    try { settings.DeviceToken = Unprotect(settings.EncryptedToken); }
                    catch (Exception error) when (error is CryptographicException or FormatException)
                    {
                        // 로그인 토큰만 손상된 경우 화면·서버·렌더링 설정까지 초기화하지 않는다.
                        settings.DeviceToken = null;
                        settings.EncryptedToken = null;
                        AppLog.Warn("settings-token", "Saved sign-in token could not be read; sign-in is required.");
                    }
                }
                return settings;
            }
            catch
            {
                return new DesktopAppSettings();
            }
        }
    }

    public void Save(DesktopAppSettings settings)
    {
        var stored = new DesktopAppSettings
        {
            ServerUrl = settings.ServerUrl,
            Username = settings.Username,
            RememberLogin = settings.RememberLogin,
            ThemeMode = settings.ThemeMode,
            UseSoftwareRendering = settings.UseSoftwareRendering,
            MessageFontSizeMode = settings.MessageFontSizeMode,
            LastReleaseNoteVersion = settings.LastReleaseNoteVersion,
            MainWindowLeft = settings.MainWindowLeft,
            MainWindowTop = settings.MainWindowTop,
            MainWindowWidth = settings.MainWindowWidth,
            MainWindowHeight = settings.MainWindowHeight,
            ChatWindowRects = settings.ChatWindowRects,
            EncryptedToken = string.IsNullOrEmpty(settings.DeviceToken) ? null : Protect(settings.DeviceToken)
        };
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        lock (IoLock)
        {
            // [P1] temp 파일 후 원자적 교체 — 크래시/강제종료 타이밍에 settings.json이 깨져
            // 서버URL·자동로그인 토큰까지 기본값으로 리셋되던 위험 제거.
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(_settingsPath))
            {
                File.Replace(tmp, _settingsPath, null);
            }
            else
            {
                File.Move(tmp, _settingsPath);
            }
        }
    }

    public void ClearToken()
    {
        var settings = Load();
        settings.DeviceToken = null;
        settings.EncryptedToken = null;
        Save(settings);
    }

    private string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private string? Unprotect(string value)
    {
        var encrypted = Convert.FromBase64String(value);
        var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}

public class DesktopAppSettings
{
    public string ServerUrl { get; set; } = "http://localhost:18080";
    public string? Username { get; set; }
    public bool RememberLogin { get; set; } = true;
    public string ThemeMode { get; set; } = "Light";
    public bool UseSoftwareRendering { get; set; }
    public string MessageFontSizeMode { get; set; } = "Small";
    public string? LastReleaseNoteVersion { get; set; }
    public double? MainWindowLeft { get; set; }
    public double? MainWindowTop { get; set; }
    public double? MainWindowWidth { get; set; }
    public double? MainWindowHeight { get; set; }
    // [1.0.85] 채팅창 룸ID별 위치/크기 — key="roomId:userIdHash"(다중 계정 보호용은 추후)
    public System.Collections.Generic.Dictionary<string, WindowRect> ChatWindowRects { get; set; } = new();
    /// 디스크에 저장되는 것은 이쪽 하나뿐이다(DPAPI, CurrentUser 범위).
    public string? EncryptedToken { get; set; }

    /// <summary>
    /// 복호화된 평문 토큰 — <b>메모리 전용</b>. 절대 파일에 쓰지 않는다.
    ///
    /// <para>지금까지 안전했던 이유는 <c>Save()</c> 가 매번 이 필드를 뺀 객체를 새로 만들어
    /// 직렬화했기 때문이다. 즉 <b>관행에만 의존</b>하고 있었고, 누가 <c>Save</c> 를
    /// "settings 를 그대로 직렬화" 로 정리하는 순간 평문 토큰이 디스크에 떨어져
    /// DPAPI 암호화가 무의미해진다. 관행 대신 타입으로 막는다.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DeviceToken { get; set; }
}

public class WindowRect
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
