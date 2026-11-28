using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Portfolio.OfficeSystem;

/// <summary>
/// 클라이언트 진단 로그.
///
/// <para>왜 필요한가 — 이 앱에는 <c>catch { return null; }</c> 형태로 원인을 버리는 자리가
/// 100곳 가까이 있다. 실패 <b>사실</b>은 화면에 표시되지만(전송 실패 배지 등) <b>이유</b>는
/// 어디에도 남지 않았다. 전역 핸들러가 쓰는 crash 로그는 <b>unhandled</b> 예외만 받으므로
/// 이 부류는 잡히지 않는다.</para>
///
/// <para>폐쇄망이라 외부 모니터링도 없다. 사용자가 "안 돼요" 라고 하면 클라이언트에 볼 것이
/// 없고, 요청이 서버에 닿지 않았다면 서버 로그에도 없다. 그 공백을 메운다.</para>
///
/// <para>로그 자체가 앱을 죽이면 안 되므로 모든 실패를 삼킨다. 개인정보·본문은 남기지 않고
/// 요청 URL·상태코드·예외 종류까지만 적는다.</para>
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Portfolio.OfficeSystem", "logs");

    public static void Warn(string context, string message)
    {
        Write("WARN", context, message);
    }

    public static void Warn(string context, Exception ex)
    {
        Write("WARN", context, ex.GetType().Name + ": " + ex.Message);
    }

    public static void Info(string context, string message)
    {
        Write("INFO", context, message);
    }

    private static void Write(string level, string context, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"client-{DateTime.Now:yyyyMMdd}.log");
            // 하루치가 무한히 자라지 않게 상한을 둔다. 넘으면 이전 파일 하나만 남기고 새로 쓴다.
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var rolled = path + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(path, rolled);
                }
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] [{level}] [{context}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 로깅 실패가 기능을 막아서는 안 된다.
        }
    }
}

/// <summary>
/// HTTP 실패를 한곳에서 남긴다.
///
/// <para>개별 <c>catch</c> 100곳을 고치는 대신 여기서 잡는다 — 현장에서 실제로 문제되는 것은
/// 대부분 전송 실패(서버 다운·방화벽·주소 오설정)와 4xx/5xx 응답이고, 둘 다 이 계층을 지난다.</para>
///
/// <para>본문은 남기지 않는다(메시지 내용·개인정보가 섞인다). 메서드·경로·상태코드만 적는다.</para>
/// </summary>
public sealed class LoggingHttpHandler : DelegatingHandler
{
    public LoggingHttpHandler(HttpMessageHandler inner) : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn("http", $"{request.Method} {Redact(request.RequestUri)} -> {(int)response.StatusCode}");
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("http", $"{request.Method} {Redact(request.RequestUri)} -> timeout");
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("http", $"{request.Method} {Redact(request.RequestUri)} -> {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// 쿼리스트링에는 토큰·검색어가 들어갈 수 있어 경로까지만 남긴다.
    private static string Redact(Uri? uri)
    {
        if (uri == null) return "(null)";
        return uri.GetLeftPart(UriPartial.Path);
    }
}
