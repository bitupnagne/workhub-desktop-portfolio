using System;
using System.Collections.Generic;
using System.Linq;
using System.Media;
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

    /// <summary>알림음 켜짐 여부 — 트레이 메뉴에서 바꾼다(설정 파일에 저장).</summary>
    public static bool SoundEnabled { get; set; } = true;
    private static SoundPlayer? _player;
    private static DateTime _lastSound = DateTime.MinValue;

    /// <summary>짧은 2음 차임(Sounds/notify.wav, 직접 합성 — 저작권 없음). 몰려 와도 0.8초에 한 번만.</summary>
    private static void PlaySound()
    {
        if (!SoundEnabled || (DateTime.Now - _lastSound).TotalMilliseconds < 800) return;
        _lastSound = DateTime.Now;
        try
        {
            if (_player == null)
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/Sounds/notify.wav"));
                if (info == null) return;
                _player = new SoundPlayer(info.Stream);
                _player.Load();
            }
            _player.Play(); // 비동기 재생 — UI를 막지 않는다
        }
        catch
        {
            // 소리 장치가 없거나 막힌 PC — 알림 자체는 그대로 띄운다
        }
    }

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
                PlaySound();
                return;
            }
        }
        var popup = new NotificationPopupWindow(title, sender, body, onClick, roomId);
        popup.Show();
        PlaySound();
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
            double targetTop = area.Bottom - popup.Height - 18 - (i * (popup.Height + 12));
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
        // 화면 아래로 끝까지 스르륵 내려간 뒤 닫는다(전에는 페이드가 먼저 끝나 내려가다 말고 사라졌다).
        var slide = new DoubleAnimation(area.Bottom + 10, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(180) };
        slide.Completed += (_, _) => Close();
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
