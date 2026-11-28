using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Portfolio.OfficeSystem;

// 채팅 말풍선용 1:1 cover 액자 그리드. 1/2/4=균등, 3=큰1+작은2, 5장↑=2×2 + 마지막 칸 +N.
// 그리드는 썸네일 URL을 쓰고, 클릭 시 원본 전체를 ImagePreviewWindow로 연다.
public partial class ChatImageGrid : UserControl
{
    public static readonly DependencyProperty ImagesProperty =
        DependencyProperty.Register(nameof(Images), typeof(IEnumerable<MessageAttachmentModel>), typeof(ChatImageGrid),
            new PropertyMetadata(null, (d, e) =>
            {
                if (d is ChatImageGrid grid) grid.Render(e.NewValue as IEnumerable<MessageAttachmentModel>);
            }));

    public IEnumerable<MessageAttachmentModel>? Images
    {
        get => (IEnumerable<MessageAttachmentModel>?)GetValue(ImagesProperty);
        set => SetValue(ImagesProperty, value);
    }

    private const double Frame = 232;
    private List<MessageAttachmentModel> _all = new();

    public ChatImageGrid()
    {
        InitializeComponent();
    }

    private void Render(IEnumerable<MessageAttachmentModel>? images)
    {
        root.Children.Clear();
        root.ColumnDefinitions.Clear();
        root.RowDefinitions.Clear();

        _all = images?.Where(a => a != null).ToList() ?? new List<MessageAttachmentModel>();
        int count = _all.Count;
        if (count == 0) { Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;

        int n = System.Math.Min(4, count);
        int more = count - n;

        // 장수별 그리드 크기/셀 배치 (col,row,colSpan,rowSpan)
        (int col, int row, int colSpan, int rowSpan)[] cells;
        switch (n)
        {
            case 1:
                root.Width = Frame; root.Height = Frame;
                AddCols(1); AddRows(1);
                cells = new[] { (0, 0, 1, 1) };
                break;
            case 2:
                root.Width = Frame; root.Height = Frame / 2;
                AddCols(2); AddRows(1);
                cells = new[] { (0, 0, 1, 1), (1, 0, 1, 1) };
                break;
            case 3:
                root.Width = Frame; root.Height = Frame;
                AddColsWeighted(2, 1); AddRows(2);
                cells = new[] { (0, 0, 1, 2), (1, 0, 1, 1), (1, 1, 1, 1) };
                break;
            default: // 4 (or +N)
                root.Width = Frame; root.Height = Frame;
                AddCols(2); AddRows(2);
                cells = new[] { (0, 0, 1, 1), (1, 0, 1, 1), (0, 1, 1, 1), (1, 1, 1, 1) };
                break;
        }

        for (int i = 0; i < n; i++)
        {
            var att = _all[i];
            var (col, row, colSpan, rowSpan) = cells[i];

            var tile = new Border
            {
                Margin = new Thickness(1),
                Background = (Brush)FindResource("Gray200Brush"),
                ClipToBounds = true,
                Cursor = Cursors.Hand
            };
            Grid.SetColumn(tile, col);
            Grid.SetRow(tile, row);
            Grid.SetColumnSpan(tile, colSpan);
            Grid.SetRowSpan(tile, rowSpan);

            var cell = new Grid();
            var img = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            WebImage.SetUrl(img, att.ThumbnailUrl);
            cell.Children.Add(img);

            if (i == n - 1 && more > 0)
            {
                var overlay = new Border { Background = new SolidColorBrush(Color.FromArgb(0x7A, 0x0F, 0x17, 0x2A)) };
                overlay.Child = new TextBlock
                {
                    Text = $"+{more}",
                    Foreground = Brushes.White,
                    FontSize = 22,
                    FontWeight = FontWeights.ExtraBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                cell.Children.Add(overlay);
            }

            tile.Child = cell;
            int index = i;
            tile.MouseLeftButtonUp += (_, e) => { OpenViewer(index); e.Handled = true; };
            root.Children.Add(tile);
        }
    }

    private void AddCols(int n)
    {
        for (int i = 0; i < n; i++) root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    private void AddColsWeighted(params double[] weights)
    {
        foreach (var w in weights) root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
    }

    private void AddRows(int n)
    {
        for (int i = 0; i < n; i++) root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    }

    private void OpenViewer(int index)
    {
        var urls = _all.Select(a => a.DownloadUrl).ToList();
        if (urls.Count == 0) return;
        ImagePreviewWindow.Show(Window.GetWindow(this), urls, System.Math.Min(index, urls.Count - 1));
    }
}
