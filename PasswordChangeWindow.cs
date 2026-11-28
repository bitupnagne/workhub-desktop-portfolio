using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Portfolio.OfficeSystem;

/// <summary>초기·재설정 비밀번호 변경을 마친 후에만 업무 화면으로 이동한다.</summary>
public sealed class PasswordChangeWindow : Window
{
    private readonly WorkSystemApiService _api;
    private readonly PasswordBox _current = new();
    private readonly PasswordBox _next = new();
    private readonly PasswordBox _confirm = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _save = new() { Content = "비밀번호 변경", IsDefault = true, Padding = new Thickness(16, 8, 16, 8) };
    private bool _saving;

    public PasswordChangeWindow(WorkSystemApiService api)
    {
        _api = api;
        Title = "비밀번호 변경";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        Foreground = Brushes.Black;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "새 비밀번호를 설정해 주세요", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "초기 또는 재설정된 비밀번호는 변경이 필요합니다.\n영문·숫자·특수문자를 포함한 9자 이상으로 입력하세요.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        AddPasswordField(panel, "현재 비밀번호", _current);
        AddPasswordField(panel, "새 비밀번호", _next);
        AddPasswordField(panel, "새 비밀번호 확인", _confirm);
        panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "로그인 취소", IsCancel = true, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        panel.Children.Add(buttons);
        Content = panel;
        _save.Click += async (_, _) => await SaveAsync();
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        Loaded += (_, _) => _current.Focus();
        Closed += (_, _) => { _current.Clear(); _next.Clear(); _confirm.Clear(); };
    }

    private static void AddPasswordField(Panel panel, string label, PasswordBox input)
    {
        var caption = new Label { Content = label, Target = input, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(caption);
        input.Padding = new Thickness(8);
        input.Margin = new Thickness(0, 0, 0, 14);
        panel.Children.Add(input);
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        if (string.IsNullOrEmpty(_current.Password) || string.IsNullOrEmpty(_next.Password))
        {
            _error.Text = "현재 비밀번호와 새 비밀번호를 입력해 주세요.";
            return;
        }
        if (_next.Password != _confirm.Password)
        {
            _error.Text = "새 비밀번호와 확인 값이 일치하지 않습니다.";
            return;
        }
        _saving = true;
        _save.IsEnabled = false;
        _error.Text = "변경 중...";
        var (success, message) = await _api.ChangePasswordAsync(_current.Password, _next.Password, _confirm.Password);
        _saving = false;
        _save.IsEnabled = true;
        if (success) DialogResult = true;
        else _error.Text = message ?? "비밀번호를 변경하지 못했습니다. 다시 시도해 주세요.";
    }
}
