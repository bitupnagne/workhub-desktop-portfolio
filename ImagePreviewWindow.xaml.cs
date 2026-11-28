using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Portfolio.OfficeSystem;

public partial class ImagePreviewWindow : Window
{
    private readonly List<string> _imageUrls;
    private int _index;
    private readonly List<ThumbItem> _thumbs = new();

    public ImagePreviewWindow(string imageUrl)
        : this(new[] { imageUrl }, 0)
    {
    }

    public ImagePreviewWindow(IEnumerable<string> imageUrls, int startIndex = 0)
    {
        InitializeComponent();
        // 화면을 가득 채우지 않고 웹 모달처럼 적당한 크기의 중앙 카드로 띄운다.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(960, wa.Width - 40);
        Height = Math.Min(760, wa.Height - 40);
        _imageUrls = imageUrls.Where(url => !string.IsNullOrWhiteSpace(url) && url != "about:blank").Distinct().ToList();
        _index = Math.Clamp(startIndex, 0, Math.Max(0, _imageUrls.Count - 1));
        foreach (var url in _imageUrls) _thumbs.Add(new ThumbItem { Url = url });
        thumbList.ItemsSource = _thumbs;
        thumbStrip.Visibility = _imageUrls.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowCurrent();
        PreviewKeyDown += (s, e) => {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Left) { Move(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { Move(1); e.Handled = true; }
        };
    }

    public static void Show(Window? owner, string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl) || imageUrl == "about:blank") return;
        var win = new ImagePreviewWindow(imageUrl);
        if (owner != null) win.Owner = owner;
        win.Show();
    }

    public static void Show(Window? owner, IEnumerable<string> imageUrls, int startIndex = 0)
    {
        var urls = imageUrls.Where(url => !string.IsNullOrWhiteSpace(url) && url != "about:blank").ToList();
        if (urls.Count == 0) return;
        var win = new ImagePreviewWindow(urls, startIndex);
        if (owner != null) win.Owner = owner;
        win.Show();
    }

    private double _zoom = 1.0;
    private bool _panning;
    private System.Windows.Point _panStart;
    private double _txStart, _tyStart;

    private void ResetZoom()
    {
        _zoom = 1.0;
        if (imgScale != null) { imgScale.ScaleX = 1.0; imgScale.ScaleY = 1.0; }
        if (imgTranslate != null) { imgTranslate.X = 0; imgTranslate.Y = 0; }
    }

    private void ApplyZoom()
    {
        imgScale.ScaleX = imgScale.ScaleY = _zoom;
        if (_zoom <= 1.0001) { imgTranslate.X = 0; imgTranslate.Y = 0; }
    }

    private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 1.0, 6.0);
        ApplyZoom();
        e.Handled = true;
    }

    private void Image_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _zoom = _zoom > 1.0001 ? 1.0 : 2.0; // 더블클릭: 1x ↔ 2x
            ApplyZoom();
            e.Handled = true;
            return;
        }
        if (_zoom > 1.0001)
        {
            _panning = true;
            _panStart = e.GetPosition(this);
            _txStart = imgTranslate.X; _tyStart = imgTranslate.Y;
            imgPreview.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Image_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(this);
        imgTranslate.X = _txStart + (p.X - _panStart.X);
        imgTranslate.Y = _tyStart + (p.Y - _panStart.Y);
    }

    private void Image_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) { _panning = false; imgPreview.ReleaseMouseCapture(); e.Handled = true; }
    }

    private void ShowCurrent()
    {
        if (_imageUrls.Count == 0) return;
        ResetZoom();
        WebImage.SetUrl(imgPreview, _imageUrls[_index]);
        var multi = _imageUrls.Count > 1;
        btnPrev.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        btnNext.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        txtTitle.Text = multi ? $"이미지 {_index + 1}/{_imageUrls.Count}" : "이미지 보기";
        for (int i = 0; i < _thumbs.Count; i++) _thumbs[i].IsSelected = (i == _index);
    }

    private void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ThumbItem item)
        {
            int i = _thumbs.IndexOf(item);
            if (i >= 0) { _index = i; ShowCurrent(); }
            e.Handled = true;
        }
    }

    private void Move(int delta)
    {
        if (_imageUrls.Count <= 1) return;
        _index = (_index + delta + _imageUrls.Count) % _imageUrls.Count;
        ShowCurrent();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void Prev_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Move(-1);
        e.Handled = true;
    }

    private void Next_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Move(1);
        e.Handled = true;
    }
}

public class ThumbItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string? Url { get; set; }
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected))); }
    }
}
