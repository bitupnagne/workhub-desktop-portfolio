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

// [W6] MainWindow 분리 — 테마·브랜딩(로고 캐시)·사이드바 아이콘. 동작 변경 없음(파일 분리만).
public partial class MainWindow
{
    private async Task ApplySystemThemeAsync()
    {
        var theme = await _apiService.GetSystemThemeAsync();
        if (theme != null)
        {
            ThemeService.ApplySystemTheme(theme);
            StartupThemeChanged?.Invoke(theme);
        }
    }

    private async Task ApplySystemBrandingAsync(string serverUrl)
    {
        var branding = await _apiService.GetSystemBrandingAsync();
        var logoUrl = branding?.DesktopLogoUrl;
        if (string.IsNullOrWhiteSpace(logoUrl)) return;
        if (!logoUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !logoUrl.Contains("/system-logo/desktop", StringComparison.OrdinalIgnoreCase))
            return;

        var baseUrl = AppDefaults.NormalizeServerUrl(serverUrl);
        var absolute = logoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? logoUrl
            : baseUrl + (logoUrl.StartsWith("/") ? logoUrl : "/" + logoUrl);
        var source = await CacheRemoteSvgLogoAsync(absolute);
        HeaderAppLogo.Source = source;
        LoadingAppLogo.Source = source;
        LoginAppLogo.Source = source;
        ProfileAppLogo.Source = source;
    }

    private async Task<Uri> CacheRemoteSvgLogoAsync(string absoluteUrl)
    {
        var cacheRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Portfolio.OfficeSystem",
            "portfolio-branding");
        System.IO.Directory.CreateDirectory(cacheRoot);

        var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(absoluteUrl));
        var hash = Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
        var target = System.IO.Path.Combine(cacheRoot, $"desktop-logo-{hash}.svg");

        try
        {
            var bytes = await _apiService.HttpClient.GetByteArrayAsync(absoluteUrl);
            await System.IO.File.WriteAllBytesAsync(target, bytes);
        }
        catch
        {
            if (!System.IO.File.Exists(target) || new System.IO.FileInfo(target).Length == 0)
                throw;
        }

        // 스플래시/업데이터가 서버 URL·해시를 몰라도 읽을 수 있게 고정 이름의 최신본을 남긴다.
        //  - desktop-logo-latest.svg : 스플래시(SharpVectors)용
        //  - desktop-logo-latest.png : 업데이터(SVG 렌더러 없음)용 래스터
        try
        {
            var latestSvg = System.IO.Path.Combine(cacheRoot, "desktop-logo-latest.svg");
            System.IO.File.Copy(target, latestSvg, true);
            WriteLatestLogoRaster(latestSvg, System.IO.Path.Combine(cacheRoot, "desktop-logo-latest.png"));
        }
        catch { }

        return new Uri(target, UriKind.Absolute);
    }

    // 업데이터용 캐시는 원본 SVG의 가로세로 비율을 유지한다(업데이터는 SVG 렌더러가 없어 PNG를 사용). 실패해도 무시.
    private static void WriteLatestLogoRaster(string svgPath, string pngPath)
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var settings = new SharpVectors.Renderers.Wpf.WpfDrawingSettings { IncludeRuntime = true, TextAsGeometry = true };
                using var reader = new SharpVectors.Converters.FileSvgReader(settings);
                var drawing = reader.Read(svgPath);
                if (drawing == null) return;
                var bounds = drawing.Bounds;
                if (bounds.Width <= 0 || bounds.Height <= 0) return;

                const int size = 512;
                double scale = Math.Min(size / bounds.Width, size / bounds.Height);
                int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
                int height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new System.Windows.Media.ScaleTransform(scale, scale));
                    dc.PushTransform(new System.Windows.Media.TranslateTransform(-bounds.X, -bounds.Y));
                    dc.DrawDrawing(drawing);
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(pngPath);
                encoder.Save(fs);
            });
        }
        catch { }
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Toggle();
        var settings = _settingsService.Load();
        settings.ThemeMode = ThemeService.Current.ToString();
        _settingsService.Save(settings);
        ForceRebindConverterBoundLists();
        ApplySidebarIconsForTheme();
    }

    private void ApplySidebarIconsForTheme()
    {
        // SVG 아이콘은 stroke 색을 파일에 박아두어 테마 토큰을 따라가지 않는다.
        // 다크 모드에서는 -light 변형(밝은 stroke)을 사용해 가독성을 확보.
        bool dark = ThemeService.Current == Portfolio.OfficeSystem.ThemeMode.Dark;
        SwapIcon(sideIconCalendar, "calendar", dark);
        SwapIcon(sideIconMessageCircle, "message-circle", dark);
        SwapIcon(sideIconDataTransfer, "data-transfer", dark);
        SwapIcon(sideIconUsers, "users", dark);
        SwapIcon(sideIconOrgChart, "org-chart", dark);
        SwapIcon(sideIconDocuments, "documents", dark);
        SwapIcon(headerIconBell, "bell", dark);
        SwapIcon(emptyIconBellOff, "bell-off", dark);
        SwapIcon(emptyIconMessageSquare, "message-square", dark);
    }

    private static void SwapIcon(SharpVectors.Converters.SvgViewbox? box, string baseName, bool dark)
    {
        if (box == null) return;
        try { box.Source = new Uri($"/Icons/{baseName}{(dark ? "-light" : "")}.svg", UriKind.Relative); }
        catch { }
    }

    private void ForceRebindConverterBoundLists()
    {
        // Converters cache results; we need to rebind ItemsSource so converters re-run with new theme tokens.
        if (_fullOrgTree.Count > 0)
        {
            var query = txtOrgSearch.Text;
            trvOrg.ItemsSource = null;
            FilterOrgTree(query);
        }
        // _rooms 는 ObservableCollection — 테마 토큰 변경 시 카드 ItemTemplate의 DataTrigger가
        // 자동으로 재평가되므로 별도 강제 리바인드 불필요.
        if (lstWorkMemos.ItemsSource is System.Collections.IEnumerable memos)
        {
            lstWorkMemos.ItemsSource = null;
            lstWorkMemos.ItemsSource = memos;
        }
    }

}
