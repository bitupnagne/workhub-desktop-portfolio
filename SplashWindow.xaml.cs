using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Portfolio.OfficeSystem;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        ApplyBrandingAndVersion();
        Loaded += SplashWindow_Loaded;
    }

    // 서버에서 받아 캐시된 로고(있으면)와 실제 앱 버전을 적용. 캐시 없으면 번들 로고/빈 버전.
    private void ApplyBrandingAndVersion()
    {
        try
        {
            var latest = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Portfolio.OfficeSystem", "portfolio-branding", "desktop-logo-latest.svg");
            if (System.IO.File.Exists(latest) && new System.IO.FileInfo(latest).Length > 0)
                SplashLogo.Source = new Uri(latest, UriKind.Absolute);
        }
        catch { }

        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    public void ApplyTheme(SystemThemeModel theme)
    {
        try
        {
            var brand = (Color)ColorConverter.ConvertFromString(theme.Brand);
            var brandDark = (Color)ColorConverter.ConvertFromString(theme.BrandDark);
            SplashBorder.BorderBrush = new SolidColorBrush(brand);
            BarStart.Color = brand;
            BarMid.Color = brandDark;
        }
        catch { }
    }

    private void SplashWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Progress bar track 320px, bar width 120px → slide -120 → 320
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
}
