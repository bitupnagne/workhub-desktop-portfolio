using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Portfolio.OfficeSystem;

public partial class DocumentWindow : Window
{
    public DocumentWindow(string url, string title = "문서 보기")
    {
        InitializeComponent();
        txtTitle.Text = title;
        InitializeAsync(url);
        
        // Bring to front
        this.Topmost = true;
        this.Loaded += (s, e) => {
            this.Activate();
            this.Topmost = false; // Release topmost after activation so it doesn't block other apps forever
        };
    }

    private async void InitializeAsync(string url)
    {
        try
        {
            // WebView2 데이터 디렉터리를 %LOCALAPPDATA%\Portfolio.OfficeSystem\WebView2 로 명시한다.
            // 기본값(실행 파일 옆 = C:\Program Files\... )은 일반 사용자 쓰기 권한이 없어
            // 권한 거부 오류(데이터 디렉터리를 만들 수 없습니다 / E_UNEXPECTED)가 발생한다.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Portfolio.OfficeSystem", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(env);
            
            // Disable some browser features for a cleaner popup look
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            
            webView.NavigationCompleted += async (s, e) =>
            {
                loadingOverlay.Visibility = Visibility.Collapsed;
                // 웹 페이지 안의 "닫기" 버튼(onclick=window.close())은 WebView2에서 동작 안 함 → CSS로 숨김
                try
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        (function(){
                            var s = document.getElementById('vfWebviewHide');
                            if (s) return;
                            s = document.createElement('style');
                            s.id = 'vfWebviewHide';
                            s.textContent = 'button[onclick*=""window.close""]{display:none !important;}';
                            document.head.appendChild(s);
                        })();
                    ");
                }
                catch { }
            };

            webView.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            MessageBox.Show("문서를 불러오는 중 오류가 발생했습니다: " + ex.Message);
            this.Close();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        loadingOverlay.Visibility = Visibility.Visible;
        webView.Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            try { this.DragMove(); } catch { }
        }
    }
}
