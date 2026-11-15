using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Portfolio.OfficeSystem;

public partial class MemberDetailWindow : Window
{
    private EmployeeModel? _emp;
    private long _myUserId;

    // 1:1 대화 버튼 클릭 시 대상 직원을 알린다. 창을 연 쪽이 1:1 방을 열도록 위임.
    public event System.Action<EmployeeModel>? DirectChatRequested;

    public MemberDetailWindow(EmployeeModel emp, string? heading = null, long myUserId = 0)
    {
        InitializeComponent();
        _myUserId = myUserId;
        if (!string.IsNullOrEmpty(heading))
        {
            Title = heading;
            txtHeader.Text = heading;
        }
        UpdateMember(emp);
        PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private void DirectChat_Click(object sender, RoutedEventArgs e)
    {
        if (_emp != null) DirectChatRequested?.Invoke(_emp);
        Close();
    }

    public void UpdateMember(EmployeeModel emp)
    {
        _emp = emp;
        // 본인이 아니고 myUserId 를 아는 경우에만 1:1 대화 버튼 노출
        btnDirectChat.Visibility = (_myUserId > 0 && emp.UserId != _myUserId) ? Visibility.Visible : Visibility.Collapsed;
        txtName.Text = emp.Name ?? "";
        txtDept.Text = emp.TeamName ?? "";
        txtJob.Text = string.IsNullOrEmpty(emp.JobTitle) ? "-" : emp.JobTitle;
        txtExt.Text = string.IsNullOrEmpty(emp.ExtensionNumber) ? "-" : emp.ExtensionNumber;
        txtMobile.Text = string.IsNullOrEmpty(emp.MobileNumber) ? "-" : emp.MobileNumber;
        txtEmail.Text = string.IsNullOrEmpty(emp.Email) ? "-" : emp.Email;
        WebImage.SetUrl(imgMember, !string.IsNullOrEmpty(emp.ProfileImageFilename) ? emp.ProfileImageUrl : null);
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenImage_Click(object sender, MouseButtonEventArgs e)
    {
        var url = WebImage.GetUrl(imgMember);
        if (!string.IsNullOrEmpty(url))
        {
            ImagePreviewWindow.Show(this, url);
            e.Handled = true;
        }
    }
}
