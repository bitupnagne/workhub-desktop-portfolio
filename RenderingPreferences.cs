using System.Windows.Interop;

namespace Portfolio.OfficeSystem;

internal static class RenderingPreferences
{
    // 다른 PC의 기본 렌더링은 유지하고, 호환 모드는 이 앱의 사용자 프로필에만 저장한다.
    internal static RenderMode ResolveMode(string[] arguments, AppSettingsService settingsService)
    {
        var settings = settingsService.Load();
        bool? saveSoftware = null;
        foreach (string argument in arguments)
        {
            if (string.Equals(argument, "--save-rendering-mode=software", StringComparison.OrdinalIgnoreCase)) saveSoftware = true;
            else if (string.Equals(argument, "--save-rendering-mode=default", StringComparison.OrdinalIgnoreCase)) saveSoftware = false;
        }

        if (saveSoftware.HasValue && settings.UseSoftwareRendering != saveSoftware.Value)
        {
            settings.UseSoftwareRendering = saveSoftware.Value;
            try { settingsService.Save(settings); }
            catch (Exception error)
            {
                // 저장이 실패해도 현재 실행에는 요청한 호환 모드를 적용한다.
                AppLog.Warn("rendering-preference", error);
            }
        }

        bool transientSoftware = arguments.Contains("--software-rendering", StringComparer.OrdinalIgnoreCase);
        return transientSoftware || settings.UseSoftwareRendering ? RenderMode.SoftwareOnly : RenderMode.Default;
    }
}
