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

// [W6] MainWindow 분리 — 출퇴근 체크 위젯(상태 텍스트·경과시간 계산). 동작 변경 없음(파일 분리만).
public partial class MainWindow
{
    private DateTime _attendanceDate = DateTime.MinValue;
    private DateTime _attendanceLastAttempt = DateTime.MinValue;
    private bool _attendanceRefreshInProgress;

    private async Task RefreshAttendanceAsync()
    {
        if (_attendanceRefreshInProgress || mainView.Visibility != Visibility.Visible || _myUserId == 0) return;
        _attendanceRefreshInProgress = true;
        _attendanceLastAttempt = DateTime.Now;
        long userId = _myUserId;
        var date = DateTime.Today;
        try
        {
            var record = await _apiService.GetTodayAttendanceAsync();
            if (_myUserId == userId && mainView.Visibility == Visibility.Visible && date == DateTime.Today && record != null)
                UpdateAttendanceUI(record);
        }
        finally { _attendanceRefreshInProgress = false; }
    }

    private void UpdateAttendanceUI(AttendanceRecord? record)
    {
        _currentAttendance = record;
        UpdateUnrequestedOvertime(record);
        if (record == null)
        {
            btnCheckIn.IsEnabled = false;
            btnCheckOut.IsEnabled = false;
            attendanceButtonPanel.Visibility = Visibility.Visible;
            attendanceDonePanel.Visibility = Visibility.Collapsed;
            chipCheckIn.Visibility = Visibility.Collapsed;
            chipCheckOut.Visibility = Visibility.Collapsed;
            txtAttendanceStatus.Text = "근태 확인 중";
            return;
        }
        _attendanceDate = DateTime.Today;
        // [W8] 웹과 동일 — 휴무일·휴직·승인 출장/휴가면 출근 버튼 사전 비활성 + 사유 툴팁
        bool blocked = !string.IsNullOrWhiteSpace(record.BlockReason);
        btnCheckIn.IsEnabled = !record.IsCheckedIn && !blocked;
        btnCheckIn.ToolTip = blocked ? record.BlockReason : null;
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(btnCheckIn, true);
        btnCheckOut.IsEnabled = record.IsCheckedIn && !record.IsCheckedOut;
        // [OT] 휴무일·기준 퇴근시각 이후·퇴근 후 재출근은 시간외 기록으로 남는다 — 웹과 같이 버튼 문구를 바꿔 알린다.
        bool overtimeOpen = record.IsOvertimeOpen;
        bool overtimeButtons = overtimeOpen || record.IsOvertimeMode;
        if (overtimeOpen)
        {
            btnCheckIn.IsEnabled = false;
            btnCheckOut.IsEnabled = true;
        }
        else if (record.IsOvertimeMode)
        {
            btnCheckIn.IsEnabled = true;
            btnCheckOut.IsEnabled = false;
        }
        btnCheckIn.Content = overtimeButtons ? "시간외 출근" : "출근";
        btnCheckOut.Content = overtimeButtons ? "시간외 퇴근" : "퇴근";
        btnCheckIn.Width = btnCheckOut.Width = overtimeButtons ? 92 : 64;
        bool done = record.IsCheckedOut && !overtimeButtons;
        attendanceButtonPanel.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        attendanceDonePanel.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        UpdateAttendanceStatusText();
    }

    private void UpdateAttendanceStatusText()
    {
        var record = _currentAttendance;
        if (record == null || txtAttendanceStatus == null) return;

        string? badgeKey;
        if (record.IsOvertimeOpen)
        {
            var elapsed = CalcElapsed(record.OvertimeCheckInAt);
            txtAttendanceStatus.Text = elapsed.HasValue
                ? $"시간외 근무중 · {FormatElapsed(elapsed.Value)}"
                : "시간외 근무중";
            badgeKey = "SuccessLightBrush";
        }
        else if (record.IsCheckedOut)
        {
            var worked = CalcSpan(record.CheckInAt, record.CheckOutAt);
            txtAttendanceStatus.Text = worked.HasValue
                ? $"퇴근 완료 · {FormatElapsed(worked.Value)} 근무"
                : "퇴근 완료";
            badgeKey = "Gray100Brush";
        }
        else if (record.IsCheckedIn)
        {
            var elapsed = CalcElapsed(record.CheckInAt);
            txtAttendanceStatus.Text = elapsed.HasValue
                ? $"근무중 · {FormatElapsed(elapsed.Value)}"
                : "근무중";
            badgeKey = "SuccessLightBrush";
        }
        else if (!string.IsNullOrWhiteSpace(record.BlockReason))
        {
            // [W8] 오늘은 출근 체크 대상이 아님(휴무일·휴직·승인 출장/휴가 등) — 웹과 동일 안내
            txtAttendanceStatus.Text = record.BlockReason;
            badgeKey = "Gray100Brush";
        }
        else
        {
            // [W7] 오늘 기준 근무시간(요일별 근무시간표·시차출근 반영) 병기 — 서버 1.0.25+ 응답에만 존재
            txtAttendanceStatus.Text = string.IsNullOrWhiteSpace(record.ScheduleText)
                ? "근무 대기중"
                : $"근무 대기중 · 기준 {record.ScheduleText}";
            badgeKey = "BrandLightBrush";
        }

        if (statusBadge != null && TryFindResource(badgeKey) is System.Windows.Media.Brush brush)
        {
            statusBadge.Background = brush;
        }

        if (chipCheckIn != null)
        {
            if (record.IsCheckedIn)
            {
                chipCheckIn.Visibility = Visibility.Visible;
                txtCheckInTime.Text = ShortTime(record.CheckInAt) ?? "--:--";
            }
            else chipCheckIn.Visibility = Visibility.Collapsed;
        }
        if (chipCheckOut != null)
        {
            if (record.IsCheckedOut)
            {
                chipCheckOut.Visibility = Visibility.Visible;
                txtCheckOutTime.Text = ShortTime(record.CheckOutAt) ?? "--:--";
            }
            else chipCheckOut.Visibility = Visibility.Collapsed;
        }
    }

    private static string? ShortTime(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        if (DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
            return dt.ToString("HH:mm");
        return iso.Length >= 16 ? iso.Substring(11, 5) : iso;
    }

    private static TimeSpan? CalcElapsed(string? checkInIso)
    {
        if (string.IsNullOrEmpty(checkInIso)) return null;
        if (!DateTime.TryParse(checkInIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var checkIn)) return null;
        var elapsed = DateTime.Now - checkIn;
        if (elapsed.TotalSeconds < 0) return null;
        return elapsed;
    }

    private static TimeSpan? CalcSpan(string? fromIso, string? toIso)
    {
        if (string.IsNullOrEmpty(fromIso) || string.IsNullOrEmpty(toIso)) return null;
        if (!DateTime.TryParse(fromIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var from)) return null;
        if (!DateTime.TryParse(toIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var to)) return null;
        var span = to - from;
        return span.TotalSeconds >= 0 ? span : (TimeSpan?)null;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        int hours = (int)elapsed.TotalHours;
        int minutes = elapsed.Minutes;
        if (hours == 0) return $"{minutes}분";
        return $"{hours}시간 {minutes}분";
    }

    /// <summary>
    /// 확인 창 문구 — 서버 판정 미리보기(정상·지각·조퇴·외근 유예·시간외 등, 웹과 같은 값)를 보여 준다.
    /// 막힌 경우(allowed=false)면 사유를 알리고 false. 미리보기를 못 받으면 기존 문구로 진행한다.
    /// </summary>
    private async Task<bool> ConfirmAttendanceAsync(string kind, string title, string fallbackQuestion, string action)
    {
        var preview = await _apiService.GetCheckPreviewAsync(kind);
        string now = preview?.NowTime ?? DateTime.Now.ToString("HH:mm:ss");
        if (preview != null && !preview.Allowed)
        {
            await ShowCustomModal(kind == "in" ? "출근 불가" : "퇴근 불가", preview.Label ?? "지금은 처리할 수 없습니다.", false);
            await RefreshAttendanceAsync();
            return false;
        }
        string body = preview?.Label is { Length: > 0 } label
            ? $"현재 시간: {now}\n판정: {label}\n\n{action} 처리하시겠습니까?"
            : $"현재 시간: {now}\n\n{fallbackQuestion}";
        return await ShowCustomModal(title, body, true);
    }

    private async void CheckIn_Click(object sender, RoutedEventArgs e)
    {
        bool overtime = _currentAttendance?.IsOvertimeMode == true;
        string title = overtime ? "시간외 출근 확인" : "출근 확인";
        string question = overtime
            ? $"{_currentAttendance!.OvertimeReason} — 시간외 기록으로 남습니다.\n시간외 출근 처리하시겠습니까?"
            : "출근 처리하시겠습니까?";
        if (await ConfirmAttendanceAsync("in", title, question, overtime ? "시간외 출근" : "출근"))
        {
            ShowLoading("출근 처리 중...");
            var (success, message) = await _apiService.PerformAttendanceAsync("/hr/attendance/check-in");
            HideLoading();
            if (success) UpdateAttendanceUI(await _apiService.GetTodayAttendanceAsync());
            // [W3] 서버 사유("이미 출근 처리되었습니다" 등)를 그대로 표시
            else
            {
                await ShowCustomModal("출근 불가", message ?? "출근 처리 중 오류가 발생했습니다.", false);
                // 거절 사유(기준 퇴근시각 경과·다른 기기에서 이미 출근 등)가 버튼 상태에도 바로 반영되게 한다.
                await RefreshAttendanceAsync();
            }
        }
    }

    private async void CheckOut_Click(object sender, RoutedEventArgs e)
    {
        bool overtime = _currentAttendance?.IsOvertimeOpen == true;
        string action = overtime ? "시간외 퇴근" : "퇴근";
        if (await ConfirmAttendanceAsync("out", overtime ? "시간외 퇴근 확인" : "퇴근 확인", $"{action} 처리하시겠습니까?", action))
        {
            ShowLoading("퇴근 처리 중...");
            var (success, message) = await _apiService.PerformAttendanceAsync("/hr/attendance/check-out");
            HideLoading();
            if (success) UpdateAttendanceUI(await _apiService.GetTodayAttendanceAsync());
            else
            {
                await ShowCustomModal("퇴근 불가", message ?? "퇴근 처리 중 오류가 발생했습니다.", false);
                await RefreshAttendanceAsync();
            }
        }
    }

    /// <summary>[OT] 미신청 시간외 안내 줄 — 건수가 있을 때만 보인다.</summary>
    private void UpdateUnrequestedOvertime(AttendanceRecord? record)
    {
        int count = record?.UnrequestedOvertimeCount ?? 0;
        unrequestedOvertimeBanner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        txtUnrequestedOvertime.Text = $"신청하지 않은 시간외 근무 {count}건 — 이번 달 안에 신청해야 인정됩니다";
    }

    private async void UnrequestedOvertime_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await OpenWebRedirectAsync("/hr/overtime?tab=unrequested");
    }

}
