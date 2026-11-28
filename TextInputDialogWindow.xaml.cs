using System.Windows;
using System.Windows.Input;

namespace Portfolio.OfficeSystem;

public partial class TextInputDialogWindow : Window
{
    public string InputText => txtInput.Text;

    public TextInputDialogWindow(string title, string message, string initialValue)
    {
        InitializeComponent();
        txtTitle.Text = title;
        txtMessage.Text = message;
        txtInput.Text = initialValue;
        Loaded += (_, _) =>
        {
            txtInput.Focus();
            txtInput.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
