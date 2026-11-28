namespace Portfolio.OfficeSystem;

/// <summary>
/// 앱 기본값 한 곳 — 서버 주소 기본값이 다섯 군데 흩어져 있어 바꿀 때마다 빠뜨리던 문제를 막는다.
/// 실제 접속 주소는 설정 파일(서버 주소 설정 화면)에서 온다. 이 값은 설정이 비었을 때만 쓴다.
/// </summary>
internal static class AppDefaults
{
    public const string ServerUrl = "http://localhost:18080";

    /// <summary>빈 값이면 기본 주소, 아니면 앞뒤 공백과 끝 '/' 를 정리한 주소.</summary>
    public static string NormalizeServerUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? ServerUrl : url.Trim().TrimEnd('/');
}
