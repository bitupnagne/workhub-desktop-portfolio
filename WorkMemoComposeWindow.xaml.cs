using Microsoft.Win32;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Portfolio.OfficeSystem;

public partial class WorkMemoComposeWindow : Window
{
    private readonly WorkSystemApiService _api;
    private readonly List<EmployeeModel> _allEmployees;
    private readonly List<OrgNodeModel> _orgTree;
    private readonly long _myUserId;
    private List<EmployeeModel> _recipients = new();
    private string _importance = "NORMAL";
    private readonly ObservableCollection<PendingFile> _attachments = new();

    public event System.Action? Sent;

    public WorkMemoComposeWindow(WorkSystemApiService api, IEnumerable<EmployeeModel> allEmployees,
        IEnumerable<OrgNodeModel> orgTree, long myUserId, string? preBody = null, IEnumerable<string>? preAttachments = null)
    {
        InitializeComponent();
        _api = api;
        _allEmployees = allEmployees?.ToList() ?? new List<EmployeeModel>();
        _orgTree = orgTree?.ToList() ?? new List<OrgNodeModel>();
        _myUserId = myUserId;
        lstAttachments.ItemsSource = _attachments;
        UpdateRecipientVisual();
        if (!string.IsNullOrEmpty(preBody)) txtBody.Text = preBody;
        if (preAttachments != null)
        {
            foreach (var p in preAttachments.Where(File.Exists))
            {
                var fi = new FileInfo(p);
                _attachments.Add(new PendingFile { Path = p, Name = fi.Name, Size = fi.Length });
            }
        }
        _attachments.CollectionChanged += (_, _) => UpdateAttachmentSummary();
        UpdateAttachmentSummary();
        PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private MemberPickerWindow? _activePicker;
    private void PickRecipients_Click(object sender, RoutedEventArgs e)
    {
        if (_activePicker != null && _activePicker.IsVisible) { _activePicker.Activate(); return; }
        var picker = new MemberPickerWindow("받는 사람 선택", "확정", _allEmployees, _orgTree, _myUserId) { Owner = this };
        _activePicker = picker;
        // ShowDialog 대신 Show + 이벤트 — 다른 창(채팅창 등) 입력이 막히지 않도록.
        picker.Confirmed += ids =>
        {
            _recipients = _allEmployees.Where(emp => ids.Contains(emp.UserId)).ToList();
            UpdateRecipientVisual();
        };
        picker.Closed += (_, _) => { if (ReferenceEquals(_activePicker, picker)) _activePicker = null; };
        picker.Show();
    }

    private void UpdateRecipientVisual()
    {
        lstRecipientChips.ItemsSource = _recipients;
        txtRecipientCount.Text = $"{_recipients.Count}명 선택";
        txtRecipientEmpty.Visibility = _recipients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // [1.0.78] 중요도 개념 폐기 — 즐겨찾기(별표) 로 통합. _importance 는 호환을 위해 NORMAL 고정.

    private void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        // [1.0.88] ChatRoomWindow 패턴으로 단순화 — STA 별도 스레드 + Join 으로 인한
        // UI 컴포지터 quota 부족(ERROR_NOT_ENOUGH_QUOTA 1816) 문제 해결.
        try
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = "첨부파일 선택",
                Filter = "모든 파일|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;

            long total = _attachments.Sum(f => f.Size);
            foreach (var path in dialog.FileNames.Where(File.Exists))
            {
                var fi = new FileInfo(path);
                total += fi.Length;
                if (total > 100L * 1024L * 1024L)
                {
                    MessageBox.Show(this, "첨부파일은 총 100MB까지만 가능합니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _attachments.Add(new PendingFile { Path = path, Name = fi.Name, Size = fi.Length });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AddAttachment] {ex}");
            MessageBox.Show(this, "첨부파일 추가 중 오류: " + ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PendingFile f) _attachments.Remove(f);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var title = txtTitle.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(title)) { Status("제목을 입력하세요."); return; }
        if (_recipients.Count == 0) { Status("받는 사람을 선택하세요."); return; }
        btnSend.IsEnabled = false;
        ShowProgress(true);
        Status(_attachments.Count > 0 ? "업로드 중... 0%" : "보내는 중...");

        var progress = new Progress<double>(p =>
        {
            // ProgressableContent 는 이미 0..100 (퍼센트)을 보고함. *100 하면 10000% 되는 버그.
            int pct = (int)Math.Round(p);
            if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
            prgUpload.Value = pct;
            Status($"업로드 중... {pct}%");
        });

        try
        {
            var paths = _attachments.Select(f => f.Path).ToList();
            var result = await _api.CreateWorkMemoExAsync(
                title, txtBody.Text,
                _recipients.Select(r => r.UserId).ToList(),
                _importance, paths,
                _attachments.Count > 0 ? progress : null);
            if (result.Success && result.Id.HasValue)
            {
                Sent?.Invoke();
                Close();
            }
            else
            {
                Status(result.ErrorMessage ?? "전송 실패. 잠시 후 다시 시도해 주세요.");
                btnSend.IsEnabled = true;
                ShowProgress(false);
            }
        }
        catch (Exception ex)
        {
            Status("전송 중 오류: " + ex.Message);
            btnSend.IsEnabled = true;
            ShowProgress(false);
        }
    }

    private void ShowProgress(bool show)
    {
        if (prgUpload == null) return;
        prgUpload.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) prgUpload.Value = 0;
    }

    private void UpdateAttachmentSummary()
    {
        if (txtAttachmentSummary == null) return;
        if (_attachments.Count == 0) { txtAttachmentSummary.Text = ""; return; }
        long total = _attachments.Sum(f => f.Size);
        txtAttachmentSummary.Text = $"{_attachments.Count}개 / {FormatSize(total)}";
    }

    private static string FormatSize(long size)
    {
        if (size < 1024) return $"{size} B";
        if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
        if (size < 1024L * 1024 * 1024) return $"{size / 1024.0 / 1024.0:F1} MB";
        return $"{size / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    private void Status(string text) => txtStatus.Text = text;

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public class PendingFile
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public string SizeLabel
        {
            get
            {
                if (Size < 1024) return $"{Size} B";
                if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
                if (Size < 1024L * 1024 * 1024) return $"{Size / 1024.0 / 1024.0:F1} MB";
                return $"{Size / 1024.0 / 1024.0 / 1024.0:F2} GB";
            }
        }
    }
}
