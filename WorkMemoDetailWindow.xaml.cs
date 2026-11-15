using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Portfolio.OfficeSystem;

public partial class WorkMemoDetailWindow : Window
{
    private readonly WorkSystemApiService _api;
    private WorkMemoModel? _memo;

    public WorkMemoDetailWindow(WorkSystemApiService api)
    {
        InitializeComponent();
        _api = api;
        PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public async Task LoadAsync(long memoId)
    {
        var data = await _api.GetWorkMemoDetailAsync(memoId);
        if (data == null) return;
        var root = data.Value;
        if (!root.TryGetProperty("memo", out var m)) return;

        _memo = new WorkMemoModel
        {
            Id = m.GetProperty("id").GetInt64(),
            SenderUserId = m.TryGetProperty("senderUserId", out var su) ? su.GetInt64() : 0,
            SenderName = m.TryGetProperty("senderName", out var sn) && sn.ValueKind == System.Text.Json.JsonValueKind.String ? sn.GetString() : "",
            Title = m.TryGetProperty("title", out var t) ? t.GetString() : "",
            Body = m.TryGetProperty("body", out var b) ? b.GetString() : "",
            Importance = m.TryGetProperty("importance", out var imp) ? imp.GetString() : "NORMAL",
            SentAt = m.TryGetProperty("sentAt", out var sa) && sa.ValueKind == System.Text.Json.JsonValueKind.String && System.DateTime.TryParse(sa.GetString(), out var dt) ? dt : null,
            IsStarred = m.TryGetProperty("starred", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.True,
            Attachments = ParseAttachments(m)
        };

        txtTitle.Text = _memo.Title ?? "";
        txtBody.Text = _memo.Body ?? "";
        txtSender.Text = _memo.SenderName ?? "";
        txtSentAt.Text = _memo.SentAtLabel;
        UpdateStarVisual();

        if (root.TryGetProperty("recipients", out var rec) && rec.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var r in rec.EnumerateArray())
            {
                if (r.TryGetProperty("recipientName", out var rn) && rn.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var name = rn.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
                }
            }
            txtRecipients.Text = string.Join(", ", names);
        }
        lstAttachments.ItemsSource = _memo.Attachments;
    }

    private List<WorkMemoAttachmentModel> ParseAttachments(System.Text.Json.JsonElement memo)
    {
        var list = new List<WorkMemoAttachmentModel>();
        if (!memo.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
        foreach (var a in attachments.EnumerateArray())
        {
            list.Add(new WorkMemoAttachmentModel
            {
                Id = a.GetProperty("id").GetInt64(),
                MemoId = a.TryGetProperty("memoId", out var mi) ? mi.GetInt64() : 0,
                OriginalFilename = a.TryGetProperty("originalFilename", out var of) ? of.GetString() : "",
                ContentType = a.TryGetProperty("contentType", out var ct) && ct.ValueKind == System.Text.Json.JsonValueKind.String ? ct.GetString() : null,
                FileSize = a.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : 0,
                AttachmentType = a.TryGetProperty("attachmentType", out var at) ? at.GetString() : "FILE"
            });
        }
        return list;
    }

    private void UpdateStarVisual()
    {
        if (_memo == null) return;
        txtStar.Text = _memo.IsStarred ? "★" : "☆";
        if (_memo.IsStarred)
        {
            txtStar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)); // 노란 별
        }
        else
        {
            txtStar.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text3Brush");
        }
    }

    private void StarBorder_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        // 부모 헤더 DockPanel 이 MouseLeftButtonDown 으로 DragMove 를 호출해서
        // ButtonUp 이벤트를 못 받아 별표 클릭이 무반응이 되는 문제 차단.
        e.Handled = true;
    }

    private async void ToggleStar_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_memo == null) return;
        var now = await _api.ToggleWorkMemoStarAsync(_memo.Id);
        _memo.IsStarred = now;
        UpdateStarVisual();
    }

    private async void Attachment_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkMemoAttachmentModel att) return;
        // 이미지면 미리보기, 아니면 다운로드 후 열기
        if (att.IsImage)
        {
            ImagePreviewWindow.Show(this, att.DownloadUrl);
            return;
        }
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "Portfolio.OfficeSystem", "work-memo", $"{att.Id}_{att.OriginalFilename}");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            using (var http = new System.Net.Http.HttpClient())
            {
                var bytes = await http.GetByteArrayAsync(att.DownloadUrl);
                File.WriteAllBytes(temp, bytes);
            }
            Process.Start(new ProcessStartInfo(temp) { UseShellExecute = true });
        }
        catch { }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
