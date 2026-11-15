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
        attendanceButtonPanel.Visibility = record.IsCheckedOut ? Visibility.Collapsed : Visibility.Visible;
        attendanceDonePanel.Visibility = record.IsCheckedOut ? Visibility.Visible : Visibility.Collapsed;
        UpdateAttendanceStatusText();
    }

    private void UpdateAttendanceStatusText()
    {
        var record = _currentAttendance;
        if (record == null || txtAttendanceStatus == null) return;

        string? badgeKey;
        if (record.IsCheckedOut)
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

    private async void CheckIn_Click(object sender, RoutedEventArgs e)
    {
        if (await ShowCustomModal("출근 확인", $"현재 시간: {DateTime.Now:HH:mm:ss}\n\n출근 처리하시겠습니까?", true))
        {
            ShowLoading("출근 처리 중...");
            var (success, message) = await _apiService.PerformAttendanceAsync("/hr/attendance/check-in");
            HideLoading();
            if (success) UpdateAttendanceUI(await _apiService.GetTodayAttendanceAsync());
            // [W3] 서버 사유("이미 출근 처리되었습니다" 등)를 그대로 표시
            else await ShowCustomModal("출근 불가", message ?? "출근 처리 중 오류가 발생했습니다.", false);
        }
    }

    private async void CheckOut_Click(object sender, RoutedEventArgs e)
    {
        if (await ShowCustomModal("퇴근 확인", $"현재 시간: {DateTime.Now:HH:mm:ss}\n\n퇴근 처리하시겠습니까?", true))
        {
            ShowLoading("퇴근 처리 중...");
            var (success, message) = await _apiService.PerformAttendanceAsync("/hr/attendance/check-out");
            HideLoading();
            if (success) UpdateAttendanceUI(await _apiService.GetTodayAttendanceAsync());
            else await ShowCustomModal("퇴근 불가", message ?? "퇴근 처리 중 오류가 발생했습니다.", false);
        }
    }

}
