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

// [W6] MainWindow 분리 — 자동 업데이트(시작/주기 확인·업데이터 교체 마무리·릴리스 노트). 동작 변경 없음(파일 분리만).
public partial class MainWindow
{
    private static void TryCompletePendingUpdaterReplacement()
    {
        try
        {
            string appDir = AppContext.BaseDirectory;
            string pendingExe = System.IO.Path.Combine(appDir, "Portfolio.OfficeSystem.Updater.pending.exe");
            if (!System.IO.File.Exists(pendingExe))
                return;

            string updaterExe = System.IO.Path.Combine(appDir, "Portfolio.OfficeSystem.Updater.exe");
            string pendingPdb = System.IO.Path.Combine(appDir, "Portfolio.OfficeSystem.Updater.pending.pdb");
            string updaterPdb = System.IO.Path.Combine(appDir, "Portfolio.OfficeSystem.Updater.pdb");
            string script = string.Join("; ", new[]
            {
                "$ErrorActionPreference='SilentlyContinue'",
                "for($i=0;$i -lt 60;$i++){ if(-not (Get-Process -Name 'Portfolio.OfficeSystem.Updater' -ErrorAction SilentlyContinue)){ break }; Start-Sleep -Milliseconds 500 }",
                $"Copy-Item -LiteralPath '{EscapePowerShellLiteral(pendingExe)}' -Destination '{EscapePowerShellLiteral(updaterExe)}' -Force",
                $"Remove-Item -LiteralPath '{EscapePowerShellLiteral(pendingExe)}' -Force",
                $"if(Test-Path -LiteralPath '{EscapePowerShellLiteral(pendingPdb)}'){{ Copy-Item -LiteralPath '{EscapePowerShellLiteral(pendingPdb)}' -Destination '{EscapePowerShellLiteral(updaterPdb)}' -Force; Remove-Item -LiteralPath '{EscapePowerShellLiteral(pendingPdb)}' -Force }}"
            });

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch
        {
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private async Task<bool> CheckForUpdateOnStartup(string serverUrl)
    {
        try
        {
            SetStartupStatus("업데이트를 확인하고 있습니다...");
            var update = await _autoUpdateService.CheckAsync(serverUrl);
            if (update == null)
            {
                // 서버에 닿지 못했으면 로그인 후 60초 뒤 다시 확인한다(필수 업데이트를 놓치지 않게).
                if (_autoUpdateService.LastCheckFailed) _startupUpdateCheckFailed = true;
                SetStartupStatus("시스템을 시작하고 있습니다...");
                await ShowReleaseNoteOnceAsync(serverUrl);
                return false;
            }

            string note = string.IsNullOrWhiteSpace(update.ReleaseNote) ? "" : $"\n\n변경 내용\n{update.ReleaseNote}";
            string message = update.Mandatory
                ? $"새 버전 {update.Version} 업데이트가 필요합니다.\n업데이트 후 앱을 다시 실행합니다.{note}"
                : $"새 버전 {update.Version}이 있습니다.\n지금 업데이트하시겠습니까?{note}";

            bool proceed = update.Mandatory || ShowStartupUpdatePrompt("업데이트 안내", message, true);
            if (!proceed) return false;

            SetStartupStatus("업데이트 파일을 확인하고 있습니다...");
            ShowLoading("업데이트 파일 확인 중...");
            var progress = new Progress<double>(value =>
            {
                string progressMessage = $"업데이트 다운로드 중... {value:0}%/100%";
                SetStartupStatus(progressMessage);
                ShowLoading(progressMessage);
            });
            string packagePath = await _autoUpdateService.DownloadAndVerifyAsync(update, progress);
            SetStartupStatus("업데이트를 설치하고 있습니다. 잠시만 기다려 주세요...");
            ShowLoading("업데이트 설치를 시작합니다...");
            _isExplicitExit = true;
            _autoUpdateService.StartInstaller(packagePath, update.PackageType ?? "FULL", GetCurrentWindowBounds());
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            HideLoading();
            // [W5] 시작 시 확인 실패(재시도 포함) — 로그인 후 60초 뒤 재확인해 mandatory 스킵 창을 좁힌다.
            _startupUpdateCheckFailed = true;
            SetStartupStatus("업데이트 오류가 발생했습니다.");
            ShowStartupUpdatePrompt("업데이트 오류", "업데이트 확인 또는 다운로드 중 오류가 발생했습니다.\n" + ex.Message, false);
            return false;
        }
    }

    private bool _startupUpdateCheckFailed;

    // 실행 중 주기적으로 업데이트를 확인해, 새 버전이 올라오면 시작 시와 동일한 프롬프트를 띄운다.
    // '나중에'를 한 번 누르면 앱을 재시작할 때까지 다시 묻지 않는다.
    private void StartPeriodicUpdateCheck(string serverUrl)
    {
        _updateServerUrl = serverUrl;
        _updateCheckTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdateWhileRunning();
        _updateCheckTimer.Start();
        // [W5] 시작 시 확인이 실패했으면 30분을 기다리지 않고 60초 뒤 한 번 재확인.
        if (_startupUpdateCheckFailed)
        {
            _startupUpdateCheckFailed = false;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                await CheckForUpdateWhileRunning();
            });
        }
    }

    private async Task CheckForUpdateWhileRunning()
    {
        if (_updateSuppressedThisSession) return;
        try
        {
            var update = await _autoUpdateService.CheckAsync(_updateServerUrl);
            if (update == null) return;

            string note = string.IsNullOrWhiteSpace(update.ReleaseNote) ? "" : $"\n\n변경 내용\n{update.ReleaseNote}";
            string message = update.Mandatory
                ? $"새 버전 {update.Version} 업데이트가 필요합니다.\n업데이트 후 앱을 다시 실행합니다.{note}"
                : $"새 버전 {update.Version}이 있습니다.\n지금 업데이트하시겠습니까?{note}";

            if (update.Mandatory)
            {
                // [U4] 사용 중 즉시 강제 진행 대신 60초 완충 — 채팅 입력 등 작성 중 내용 보호.
                try
                {
                    NotificationPopupWindow.ShowMessage("필수 업데이트", "시스템",
                        $"새 버전 {update.Version} 필수 업데이트가 60초 후 자동으로 시작됩니다.\n작성 중인 내용을 저장해 주세요.",
                        () => { });
                }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(60));
            }
            bool proceed = update.Mandatory || ShowStartupUpdatePrompt("업데이트 안내", message, true);
            if (!proceed)
            {
                _updateSuppressedThisSession = true; // '나중에' → 재시작 전까지 억제
                return;
            }

            ShowLoading("업데이트 파일 확인 중...");
            var progress = new Progress<double>(value => ShowLoading($"업데이트 다운로드 중... {value:0}%/100%"));
            string packagePath = await _autoUpdateService.DownloadAndVerifyAsync(update, progress);
            ShowLoading("업데이트 설치를 시작합니다...");
            _isExplicitExit = true;
            _autoUpdateService.StartInstaller(packagePath, update.PackageType ?? "FULL", GetCurrentWindowBounds());
            Application.Current.Shutdown();
        }
        catch
        {
            HideLoading();
        }
    }

    private async Task ShowReleaseNoteOnceAsync(string serverUrl)
    {
        try
        {
            var note = await _autoUpdateService.GetReleaseNoteAsync(serverUrl);
            if (note == null || string.IsNullOrWhiteSpace(note.Version) || string.IsNullOrWhiteSpace(note.ReleaseNote))
                return;

            var settings = _settingsService.Load();
            if (string.Equals(settings.LastReleaseNoteVersion, note.Version, StringComparison.OrdinalIgnoreCase))
                return;

            ShowStartupUpdatePrompt($"릴리스 노트 {note.Version}", note.ReleaseNote!, false, 560);
            settings.LastReleaseNoteVersion = note.Version;
            _settingsService.Save(settings);
        }
        catch
        {
            // 릴리스 노트 표시는 부가 기능이므로 실패해도 앱 실행은 계속한다.
        }
    }

    private bool ShowStartupUpdatePrompt(string title, string message, bool showCancel, double? height = null)
    {
        // 시작 단계엔 메인창이 화면 밖(-32000)에 있어, owner 기준 중앙 정렬된 다이얼로그도
        // 화면 밖에 떠 보이지 않는다. 먼저 창을 화면 안으로 복귀시킨다.
        if (this.Left <= -30000) EnsureShownCentered();
        var prompt = new UpdatePromptWindow(title, message, showCancel, height)
        {
            Owner = IsVisible && Left > -30000 && Top > -30000 ? this : null
        };
        prompt.Activate();
        return prompt.ShowDialog() == true;
    }

    private WindowBounds GetCurrentWindowBounds()
    {
        if (Left <= -30000) EnsureShownCentered();
        return new WindowBounds(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
    }

}
