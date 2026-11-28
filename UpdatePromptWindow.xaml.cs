using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Portfolio.OfficeSystem;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptWindow(string title, string message, bool showCancel, double? height = null, string? confirmText = null, string? cancelText = null)
    {
        InitializeComponent();
        if (height.HasValue) Height = height.Value;
        txtTitle.Text = title;
        txtMessage.Text = message;
        btnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (cancelText != null) btnCancel.Content = cancelText;
        btnConfirm.Content = confirmText ?? (showCancel ? "업데이트" : "닫기");
        if (!showCancel)
        {
            Grid.SetColumn(btnConfirm, 0);
            Grid.SetColumnSpan(btnConfirm, 2);
            btnConfirm.Margin = new Thickness(0);
        }
        PreviewKeyDown += (_, e) =>
        {
            if (showCancel && e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };
    }

    private void Update_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
