using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Web.WebView2.Core;

namespace Portfolio.OfficeSystem;

// [W6] MainWindow 분리 — 창 위치/크기 저장·복원(멀티모니터)과 부속 창 도킹 배치. 동작 변경 없음(파일 분리만).
public partial class MainWindow
{
    // [P2] 저장 좌표 검증은 전체 가상 데스크톱(VirtualScreen) 기준 — 주 모니터 WorkArea만 보면
    // 보조 모니터에 둔 창이 매번 "화면 밖" 판정돼 주 모니터 중앙으로 리셋됐다.
    // 모니터 구성이 바뀌어(도킹 해제 등) 실제로 화면 밖이면 종전대로 중앙 폴백.
    private static bool IsRectOnAnyScreen(double l, double t, double w, double h)
    {
        double vsLeft = SystemParameters.VirtualScreenLeft;
        double vsTop = SystemParameters.VirtualScreenTop;
        double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
        return l + w > vsLeft + 40 && l < vsRight - 40
            && t + h > vsTop + 40 && t < vsBottom - 40;
    }

    // 창을 작업영역 중앙으로 옮기고 보이게 한다. 시작 다이얼로그 표시 전/시작 완료 시 호출.
    // [1.0.82] 저장된 위치가 있고 화면 안에 들어가면 그 위치 유지, 아니면 중앙으로.
    private void EnsureShownCentered()
    {
        var wa = SystemParameters.WorkArea;
        var settings = _settingsService.Load();
        bool placed = false;
        if (settings.MainWindowLeft.HasValue && settings.MainWindowTop.HasValue
            && settings.MainWindowWidth.HasValue && settings.MainWindowHeight.HasValue)
        {
            double w = settings.MainWindowWidth.Value;
            double h = settings.MainWindowHeight.Value;
            double l = settings.MainWindowLeft.Value;
            double t = settings.MainWindowTop.Value;
            // 화면(모든 모니터) 밖이거나 너무 작으면 무시
            if (w >= MinWidth && h >= MinHeight && IsRectOnAnyScreen(l, t, w, h))
            {
                this.Width = w; this.Height = h;
                this.Left = l; this.Top = t;
                placed = true;
            }
        }
        if (!placed)
        {
            this.Left = wa.Left + Math.Max(0, (wa.Width - this.Width) / 2);
            this.Top = wa.Top + Math.Max(0, (wa.Height - this.Height) / 2);
        }
        if (this.WindowState == WindowState.Minimized) this.WindowState = WindowState.Normal;
        this.Visibility = Visibility.Visible;
        this.Activate();
    }

    private void ApplySavedChatRect(ChatRoomWindow window, long roomId)
    {
        try
        {
            var s = _settingsService.Load();
            var key = roomId.ToString();
            if (s.ChatWindowRects.TryGetValue(key, out var r))
            {
                // 최소 크기 만족 + 어느 모니터든 일정 부분이라도 들어와야 복원 [P2]
                if (r.Width >= window.MinWidth && r.Height >= window.MinHeight
                    && IsRectOnAnyScreen(r.Left, r.Top, r.Width, r.Height))
                {
                    window.Width = r.Width; window.Height = r.Height;
                    window.Left = r.Left; window.Top = r.Top;
                    return;
                }
            }
        }
        catch { }
        // 저장된 좌표가 없거나 유효하지 않으면 — 종전대로 메인창 왼쪽 도킹
        window.Left = this.Left - window.Width - 10;
        window.Top = this.Top;
    }

    // [P1] 드래그 중 픽셀 단위로 settings.json 전체 Load+Save가 초당 수십 회 돌던 I/O 폭풍 방지 —
    // 400ms 디바운스 후 한 번만 저장(창별 pending 병합, 채팅창 N개도 저장 1회).
    private System.Windows.Threading.DispatcherTimer? _posSaveTimer;
    private readonly Dictionary<long, ChatRoomWindow> _pendingChatRectSaves = new();
    private bool _mainPosSavePending;

    private void QueuePositionSave()
    {
        if (_posSaveTimer == null)
        {
            _posSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _posSaveTimer.Tick += (_, _) => { _posSaveTimer!.Stop(); FlushPositionSaves(); };
        }
        _posSaveTimer.Stop();
        _posSaveTimer.Start();
    }

    private void FlushPositionSaves()
    {
        try
        {
            var pendingChats = _pendingChatRectSaves.ToList();
            _pendingChatRectSaves.Clear();
            bool saveMain = _mainPosSavePending;
            _mainPosSavePending = false;
            if (!saveMain && pendingChats.Count == 0) return;

            var s = _settingsService.Load();
            if (saveMain && this.WindowState == WindowState.Normal
                && this.Width >= MinWidth && this.Height >= MinHeight && this.Left >= -30000)
            {
                s.MainWindowLeft = this.Left;
                s.MainWindowTop = this.Top;
                s.MainWindowWidth = this.Width;
                s.MainWindowHeight = this.Height;
            }
            foreach (var (roomId, window) in pendingChats)
            {
                try
                {
                    if (window.WindowState != WindowState.Normal) continue;
                    if (window.Width < window.MinWidth || window.Height < window.MinHeight) continue;
                    if (window.Left < -30000) continue;
                    s.ChatWindowRects[roomId.ToString()] = new WindowRect
                    {
                        Left = window.Left, Top = window.Top,
                        Width = window.Width, Height = window.Height
                    };
                }
                catch { }
            }
            _settingsService.Save(s);
        }
        catch { }
    }

    private void SaveChatRect(ChatRoomWindow window, long roomId)
    {
        _pendingChatRectSaves[roomId] = window;
        QueuePositionSave();
    }

    private void SaveMainWindowPosition()
    {
        // [P1] 디바운스 큐를 통해 저장 — 종료 시에는 OnClosing이 FlushPositionSaves로 즉시 기록.
        _mainPosSavePending = true;
        QueuePositionSave();
    }

    // 콘텐츠 높이에 맞추고, 겹치지 않게 우측(공간 없으면 좌측)에 배치한다.
    private void PositionMemberDetailWindow(Window win)
    {
        var wa = SystemParameters.WorkArea;
        double mainLeft = Left, mainTop = Top, mainW = ActualWidth;
        if (WindowState == WindowState.Maximized || double.IsNaN(mainLeft))
        {
            mainLeft = wa.Left; mainTop = wa.Top; mainW = wa.Width;
        }

        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.SizeToContent = SizeToContent.Height;   // 콘텐츠 높이만큼만
        win.Top = mainTop < wa.Top ? wa.Top : mainTop;

        const double gap = 8;
        double rightPos = mainLeft + mainW + gap;
        if (rightPos + win.Width <= wa.Right) win.Left = rightPos;                       // 우측
        else if (mainLeft - win.Width - gap >= wa.Left) win.Left = mainLeft - win.Width - gap; // 좌측
        else win.Left = Math.Max(wa.Left, wa.Right - win.Width);                          // 공간 부족 시 화면 우측 끝

        // 콘텐츠 높이 확정 후 화면 아래로 넘치면 위로 보정
        win.Loaded += (s, _) =>
        {
            var w2 = SystemParameters.WorkArea;
            if (win.Top + win.ActualHeight > w2.Bottom)
                win.Top = Math.Max(w2.Top, w2.Bottom - win.ActualHeight);
        };
    }

    // PositionMemberDetailWindow는 SizeToContent.Height 를 강제로 켜서, * 행이 있는 작성/상세 창
    // 레이아웃을 망가뜨린다. 업무전달 창들은 명시적 Width/Height 를 유지한 채 우측 도크만 한다.
    private void PositionDockedWindow(Window win)
    {
        var wa = SystemParameters.WorkArea;
        double mainLeft = Left, mainTop = Top, mainW = ActualWidth;
        if (WindowState == WindowState.Maximized || double.IsNaN(mainLeft))
        {
            mainLeft = wa.Left; mainTop = wa.Top; mainW = wa.Width;
        }
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Top = mainTop < wa.Top ? wa.Top : mainTop;
        const double gap = 8;
        double rightPos = mainLeft + mainW + gap;
        double winW = double.IsNaN(win.Width) ? 380 : win.Width;
        if (rightPos + winW <= wa.Right) win.Left = rightPos;
        else if (mainLeft - winW - gap >= wa.Left) win.Left = mainLeft - winW - gap;
        else win.Left = Math.Max(wa.Left, wa.Right - winW);
        double winH = double.IsNaN(win.Height) ? 540 : win.Height;
        if (win.Top + winH > wa.Bottom) win.Top = Math.Max(wa.Top, wa.Bottom - winH);
    }
}
