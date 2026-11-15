using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace Portfolio.OfficeSystem;

public partial class ParticipantsWindow : Window
{
    private MemberDetailWindow? _detailWindow;
    private readonly long _myUserId;

    public event System.Action<EmployeeModel>? DirectChatRequested;

    public ParticipantsWindow(IEnumerable<EmployeeModel> initialMembers, long myUserId = 0)
    {
        InitializeComponent();
        _myUserId = myUserId;
        UpdateMembers(initialMembers);
        PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => _detailWindow?.Close();
    }

    public void UpdateMembers(IEnumerable<EmployeeModel> members)
    {
        var list = new List<EmployeeModel>(members);
        lstMembers.ItemsSource = null;
        lstMembers.ItemsSource = list;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MemberRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not EmployeeModel emp) return;

        if (_detailWindow == null)
        {
            _detailWindow = new MemberDetailWindow(emp, "참여자 정보", _myUserId) { Owner = this };
            _detailWindow.Left = this.Left + this.Width + 6;
            _detailWindow.Top = this.Top;
            _detailWindow.DirectChatRequested += target => DirectChatRequested?.Invoke(target);
            _detailWindow.Closed += (_, _) => _detailWindow = null;
            _detailWindow.Show();
        }
        else
        {
            _detailWindow.UpdateMember(emp);
            _detailWindow.Activate();
        }
        e.Handled = true;
    }
}
