using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Portfolio.OfficeSystem;

public partial class NotificationPopupWindow : Window
{
    private static readonly List<NotificationPopupWindow> OpenPopups = new();
    private readonly DispatcherTimer _closeTimer = new();
    private Action _onClick;
    private bool _closingWithAnimation;
    private long? _roomId;

    public NotificationPopupWindow(string title, string sender, string body, Action onClick, long? roomId = null)
    {
        InitializeComponent();
        _roomId = roomId;
        txtTitle.Text = string.IsNullOrWhiteSpace(title) ? "새 메시지" : title;
        txtSender.Text = string.IsNullOrWhiteSpace(sender) ? "메시지" : sender;
        txtBody.Text = string.IsNullOrWhiteSpace(body) ? "" : body;
        _onClick = onClick;

        _closeTimer.Interval = TimeSpan.FromSeconds(5);
        _closeTimer.Tick += (_, _) => Close();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 18;
            // 화면 아래쪽 충분히 밑에서 시작 — 슬라이드업이 또렷하게 보이도록 시작점을 깊게 둠.
            Top = area.Bottom + Height + 40;
            Opacity = 0;
            OpenPopups.Add(this);
            RepositionPopups();
            _closeTimer.Start();
        };
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            OpenPopups.Remove(this);
            RepositionPopups();
        };
    }

    public static void ShowMessage(string title, string sender, string body, Action onClick, long? roomId = null)
    {
        // 같은 방(roomId) 토스트가 이미 떠 있으면 새로 쌓지 않고 내용만 갱신한다.
        if (roomId.HasValue)
        {
            var existing = OpenPopups.FirstOrDefault(p => p._roomId == roomId && p.IsVisible && !p._closingWithAnimation);
            if (existing != null)
            {
                existing.UpdateContent(title, sender, body, onClick);
                return;
            }
        }
        var popup = new NotificationPopupWindow(title, sender, body, onClick, roomId);
        popup.Show();
    }

    // 같은 방 새 메시지: 자리를 유지한 채 내용 갱신 + 자동 닫힘 타이머 리셋
    public void UpdateContent(string title, string sender, string body, Action onClick)
    {
        txtTitle.Text = string.IsNullOrWhiteSpace(title) ? "새 메시지" : title;
        txtSender.Text = string.IsNullOrWhiteSpace(sender) ? "메시지" : sender;
        txtBody.Text = string.IsNullOrWhiteSpace(body) ? "" : body;
        _onClick = onClick;
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    private static void RepositionPopups()
    {
        var area = SystemParameters.WorkArea;
        var visible = OpenPopups.Where(p => p.IsVisible).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            var popup = visible[i];
            double targetLeft = area.Right - popup.Width - 18;
            double targetTop = area.Bottom - popup.Height - 18 - (i * (popup.Height + 8));
            popup.BeginAnimation(LeftProperty, new DoubleAnimation(targetLeft, TimeSpan.FromMilliseconds(200)));
            // 슬라이드업 강조 — 350ms + Power 3.0 EaseOut 으로 빠르게 올라와서 부드럽게 정지.
            popup.BeginAnimation(TopProperty, new DoubleAnimation(targetTop, TimeSpan.FromMilliseconds(360))
            {
                EasingFunction = new PowerEase { Power = 3.0, EasingMode = EasingMode.EaseOut }
            });
            popup.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)));
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closingWithAnimation)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _closingWithAnimation = true;
        _closeTimer.Stop();
        var area = SystemParameters.WorkArea;
        var slide = new DoubleAnimation(area.Bottom + 10, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(TopProperty, slide);
        BeginAnimation(OpacityProperty, fade);
    }

    private void PopupCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _onClick();
        Close();
    }

    private void PopupCard_MouseEnter(object sender, MouseEventArgs e) => _closeTimer.Stop();
    private void PopupCard_MouseLeave(object sender, MouseEventArgs e) => _closeTimer.Start();
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }
}
