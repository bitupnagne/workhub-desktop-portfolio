using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Portfolio.OfficeSystem
{
    public class WorkSystemApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly HttpClientHandler _handler;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _wsCts;
        private string _baseUrl = AppDefaults.ServerUrl;
        private string _wsUrl = ToWsUrl(AppDefaults.ServerUrl);

        private static string ToWsUrl(string baseUrl)
            => baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws/live";

        public void UpdateServerUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var next = AppDefaults.NormalizeServerUrl(url);
            if (!string.Equals(next, _baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                // 다른 서버로 바꾸면 이전 서버 세션 쿠키·CSRF 를 버린다(같은 호스트의 다른 포트는 쿠키를 공유한다).
                try
                {
                    foreach (System.Net.Cookie cookie in _handler.CookieContainer.GetCookies(new Uri(_baseUrl)))
                        cookie.Expired = true;
                }
                catch { }
                ResetCsrf();
            }
            _baseUrl = next;
            _wsUrl = ToWsUrl(_baseUrl);
        }
        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
        private long _lastNotificationId = -1;
        private int _reconnectAttempt = 0;

        public string BaseUrl => _baseUrl;
        public string? LastDeviceToken { get; private set; }
        public bool MustChangePassword { get; private set; }

        public event Action<NotificationModel>? OnNotificationReceived;
        public event Action? OnSessionExpired;
        public event Action<string>? OnKicked;
        public event Action? OnPanelDataUpdated;
        public event Action<JsonElement>? OnPanelDataSnapshotUpdated;
        /// <summary>접속자 목록, 그중 '자리비움'으로 둔 사람 목록.</summary>
        public event Action<IReadOnlyList<long>, IReadOnlyList<long>>? OnPresenceChanged;
        // [O-11] 프로필 사진 변경 이벤트 — (userId, profileImageFilename)
        public event Action<long, string>? OnProfileChanged;
        // [S-1] 특정 방의 새 메시지 이벤트 — roomId
        public event Action<long>? OnMessageChanged;
        public event Action<JsonElement>? OnMessageCreated;
        public event Action? OnWebSocketConnected;

        public WorkSystemApiService()
        {
            _handler = new HttpClientHandler { UseCookies = true, AllowAutoRedirect = true };
            // [진단] 이 클래스의 catch 대부분이 원인을 버리고 null/false 만 돌려준다.
            //  실패 <사실>은 화면에 표시되지만 <이유>는 어디에도 남지 않아, 현장에서
            //  "안 돼요" 를 받으면 확인할 것이 없었다(crash 로그는 unhandled 만 받는다).
            //  개별 catch 100곳을 고치는 대신 전송 계층에서 한 번에 남긴다.
            _httpClient = new HttpClient(new LoggingHttpHandler(_handler));
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        public CookieContainer Cookies => _handler.CookieContainer;
        public HttpClient HttpClient => _httpClient;

        public async Task<LoginResult> LoginAsync(string username, string password, bool force = false)
        {
            try
            {
                var payload = new { username, password, deviceName = Environment.MachineName, force };
                var response = await PostJsonAsync($"{_baseUrl}/api/desktop/login", payload);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    string existingDevice = "다른 PC";
                    try
                    {
                        var conflictJson = await response.Content.ReadAsStringAsync();
                        using var conflictDoc = JsonDocument.Parse(conflictJson);
                        if (conflictDoc.RootElement.TryGetProperty("existingDevice", out var deviceProp))
                        {
                            existingDevice = deviceProp.GetString() ?? existingDevice;
                        }
                    }
                    catch { }
                    return LoginResult.Conflict(existingDevice);
                }
                if (!response.IsSuccessStatusCode) return LoginResult.Failed();
                var json = await response.Content.ReadAsStringAsync();
                var auth = JsonSerializer.Deserialize<DesktopAuthResponse>(json, _jsonOptions);
                if (auth?.Authenticated != true) return LoginResult.Failed();
                LastDeviceToken = auth.Token;
                MustChangePassword = auth.User?.MustChangePassword == true;
                ResetCsrf(); // 새 세션
                return LoginResult.Ok();
            }
            catch (HttpRequestException) { return LoginResult.Network(); } // [W3] 서버 다운/망 단절
            catch (TaskCanceledException) { return LoginResult.Network(); } // 타임아웃
            catch { }
            return LoginResult.Failed();
        }

        /// <summary>마지막 자동 로그인이 서버에 닿지 못해 실패했는지(토큰 문제 아님 — 토큰을 지우면 안 된다).</summary>
        public bool LastAutoLoginNetworkError { get; private set; }

        public async Task<bool> AutoLoginAsync(string token)
        {
            LastAutoLoginNetworkError = false;
            try
            {
                var response = await PostJsonAsync($"{_baseUrl}/api/desktop/refresh", new { token });
                if (!response.IsSuccessStatusCode) return false;
                var json = await response.Content.ReadAsStringAsync();
                var auth = JsonSerializer.Deserialize<DesktopAuthResponse>(json, _jsonOptions);
                if (auth?.Authenticated == true)
                {
                    LastDeviceToken = token;
                    MustChangePassword = auth.User?.MustChangePassword == true;
                    ResetCsrf(); // 새 세션
                    return true;
                }
            }
            catch (HttpRequestException) { LastAutoLoginNetworkError = true; }
            catch (TaskCanceledException) { LastAutoLoginNetworkError = true; }
            catch { }
            return false;
        }

        /// <param name="revokeToken">false 면 기기 토큰을 서버에서 폐기하지 않는다(앱 종료 — 다음 실행 때 자동 로그인 유지).</param>
        public async Task LogoutAsync(string? token = null, bool revokeToken = true)
        {
            if (!revokeToken) token = null;
            else token ??= LastDeviceToken;
            // /api/desktop/logout 은 CSRF 예외라 토큰 없이도 웹 세션을 끝낸다(token 이 있으면 기기 토큰도 폐기).
            // 예전엔 웹 /logout 을 CSRF 없이 불러 늘 403 이었고 세션이 서버에 남았다.
            try { await PostJsonAsync($"{_baseUrl}/api/desktop/logout", new { token }); } catch { }
            ResetCsrf();
            StopWebSocket();
            MustChangePassword = false;
            LastDeviceToken = null;
        }

        public async Task InitializeAuthenticatedSessionAsync()
        {
            var data = await GetRightPanelDataAsync();
            if (data.HasValue && data.Value.TryGetProperty("notifications", out var ns))
            {
                var latest = ns.EnumerateArray().FirstOrDefault();
                if (latest.ValueKind != JsonValueKind.Null && latest.ValueKind != JsonValueKind.Undefined)
                {
                    _lastNotificationId = latest.GetProperty("id").GetInt64();
                }
                else _lastNotificationId = 0;
            }
            StartWebSocket();
        }

        public async Task<(bool Success, string? Message)> ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword)
        {
            try
            {
                using var response = await PostJsonAsync($"{_baseUrl}/api/desktop/password", new { currentPassword, newPassword, confirmPassword });
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                bool ok = response.IsSuccessStatusCode && root.TryGetProperty("ok", out var result) && result.ValueKind == JsonValueKind.True;
                if (ok) MustChangePassword = false;
                var message = root.TryGetProperty("message", out var error) ? error.GetString() : null;
                return (ok, message);
            }
            catch { return (false, "서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요."); }
        }

        private async Task<HttpResponseMessage> PostJsonAsync(string url, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            // 세션이 끊기면 서버는 /login 으로 302 → 자동 리다이렉트로 로그인 화면(200)을 받는다.
            // 그대로 두면 출근·퇴근·메시지가 '성공'으로 보이지만 실제로는 아무것도 기록되지 않는다.
            if (IsLoginPageResponse(response))
            {
                response.Dispose();
                ResetCsrf();
                OnSessionExpired?.Invoke();
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"message\":\"로그인이 끊겨 처리하지 못했습니다. 다시 연결한 뒤 한 번 더 눌러 주세요.\"}",
                        Encoding.UTF8, "application/json")
                };
            }
            return response;
        }

        /// <summary>
        /// 응답이 웹 로그인 화면(/login)으로 튕겨 온 것인지 — API 경로(/api/desktop/login 등)는 해당하지 않는다.
        /// </summary>
        private static bool IsLoginPageResponse(HttpResponseMessage response)
        {
            var finalPath = response.RequestMessage?.RequestUri?.AbsolutePath?.TrimEnd('/');
            return !string.IsNullOrEmpty(finalPath)
                && !finalPath.Contains("/api/", StringComparison.OrdinalIgnoreCase)
                && finalPath.EndsWith("/login", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>[PR] 내 접속 상태 바꾸기 — "ONLINE" 또는 "AWAY". 성공하면 true.</summary>
        public async Task<bool> SetPresenceAsync(string status)
        {
            try
            {
                var response = await PostJsonAsync($"{_baseUrl}/api/desktop/presence", new { status });
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<JsonElement?> GetRightPanelDataAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/right-panel");
                // 세션이 만료/무효(서버 재시작 등)면 Spring formLogin 이 /login 으로 302 리다이렉트한다.
                // 클라이언트는 AllowAutoRedirect=true 라 200(로그인 HTML)을 받으므로, 401/403 뿐 아니라
                // "최종 URL 이 로그인 페이지" 인 경우도 세션 만료로 보고 로그아웃을 트리거한다.
                if (IsSessionExpiredResponse(response))
                {
                    ResetCsrf();
                    OnSessionExpired?.Invoke();
                    return null;
                }
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonDocument.Parse(json).RootElement;
                }
            }
            catch { }
            return null;
        }

        // 응답이 "인증 무효(세션 만료/서버 재시작)"를 의미하는지 판정.
        //  - 401/403, 또는 미인증으로 /login 페이지로 리다이렉트되어 최종 URL 이 로그인 페이지인 경우.
        private static bool IsSessionExpiredResponse(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                return true;
            return IsLoginPageResponse(response);
        }

        public async Task<SystemThemeModel?> GetSystemThemeAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/system/theme");
                if (!response.IsSuccessStatusCode) return null;
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<SystemThemeModel>(json, _jsonOptions);
            }
            catch
            {
                return null;
            }
        }

        public async Task<SystemBrandingModel?> GetSystemBrandingAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/system/branding");
                if (!response.IsSuccessStatusCode) return null;
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<SystemBrandingModel>(json, _jsonOptions);
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<CompanyScheduleModel>> GetCompanySchedulesAsync(DateTime from, DateTime to)
        {
            try
            {
                string url = $"{_baseUrl}/api/desktop/schedules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return new List<CompanyScheduleModel>();

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<CompanyScheduleModel>>(json, _jsonOptions)
                       ?? new List<CompanyScheduleModel>();
            }
            catch
            {
                return new List<CompanyScheduleModel>();
            }
        }

        public async Task<List<DesktopDocumentModel>> GetDesktopDocumentsAsync(string box)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/desktop/documents?box={Uri.EscapeDataString(box)}&limit=50");
                if (!response.IsSuccessStatusCode) return new List<DesktopDocumentModel>();
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<DesktopDocumentModel>>(json, _jsonOptions)
                       ?? new List<DesktopDocumentModel>();
            }
            catch
            {
                return new List<DesktopDocumentModel>();
            }
        }

        public async Task<JsonElement?> GetMessageStreamAsync(long roomId, int? limit = null)
        {
            try
            {
                var url = limit.HasValue && limit.Value > 0
                    ? $"{_baseUrl}/api/messages/{roomId}?limit={limit.Value}"
                    : $"{_baseUrl}/api/messages/{roomId}";
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonDocument.Parse(json).RootElement;
                }
            }
            catch { }
            return null;
        }

        /// <summary>위로 스크롤 시 과거 메시지 페이지 — beforeId 이전 limit개 + hasMore.</summary>
        public async Task<JsonElement?> GetMessagesBeforeAsync(long roomId, long beforeId, int limit)
        {
            try
            {
                var url = $"{_baseUrl}/api/messages/{roomId}/before?beforeId={beforeId}&limit={limit}";
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonDocument.Parse(json).RootElement;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// [S-1] 메시지 전송 — WebSocket 연결되어 있으면 WebSocket으로, 아니면 HTTP POST fallback.
        /// </summary>
        public async Task<bool> SendMessageAsync(long roomId, string body)
        {
            // 우선 WebSocket으로 시도 (즉시 전송)
            if (await TrySendViaWebSocket(roomId, body)) return true;
            // fallback: HTTP POST
            try
            {
                var payload = new { body = body };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/messages/{roomId}");
                request.Content = content;
                if (token != null && header != null) request.Headers.Add(header, token);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<bool> SendMessageAttachmentsAsync(long roomId, string? body, IReadOnlyList<string> filePaths, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            try
            {
                using var content = new MultipartFormDataContent();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    content.Add(new StringContent(body, Encoding.UTF8), "body");
                }
                foreach (var path in filePaths.Where(File.Exists))
                {
                    var stream = File.OpenRead(path);
                    var fileContent = new StreamContent(stream);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GuessContentType(path));
                    content.Add(fileContent, "files", Path.GetFileName(path));
                }

                var (token, header) = await GetDashboardCsrfAsync();
                HttpContent payload = progress != null ? new ProgressableContent(content, progress, ct) : content;
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/messages/{roomId}/attachments")
                {
                    Content = payload
                };
                if (token != null && header != null) request.Headers.Add(header, token);
                request.Headers.Accept.ParseAdd("application/json");

                var response = await _httpClient.SendAsync(request, ct);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) { return false; }
            catch { return false; }
        }

        public async Task<string?> DownloadMessageAttachmentAsync(MessageAttachmentModel attachment)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/messages/attachments/{attachment.Id}");
                if (!response.IsSuccessStatusCode) return null;

                var root = Path.Combine(Path.GetTempPath(), "Portfolio.OfficeSystem", "attachments");
                Directory.CreateDirectory(root);

                var filename = CleanFileName(attachment.OriginalFilename);
                var path = Path.Combine(root, $"{attachment.Id}_{filename}");
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(path);
                await source.CopyToAsync(target);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private static string GuessContentType(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".webm" => "video/webm",
                _ => "application/octet-stream"
            };
        }

        private static string CleanFileName(string? filename)
        {
            var value = string.IsNullOrWhiteSpace(filename) ? "attachment" : Path.GetFileName(filename);
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }
            return string.IsNullOrWhiteSpace(value) ? "attachment" : value;
        }

        private async Task<bool> TrySendViaWebSocket(long roomId, string body)
        {
            try
            {
                if (_webSocket == null || _webSocket.State != WebSocketState.Open) return false;
                var payload = new { type = "SEND_MESSAGE", payload = new { roomId = roomId, body = body } };
                var json = JsonSerializer.Serialize(payload);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _wsCts?.Token ?? CancellationToken.None);
                return true;
            }
            catch { return false; }
        }

        /// <summary>WebSocket 연결 상태 — 윈도우 클라이언트의 polling 일시정지 여부 판단에 사용.</summary>
        public bool IsWebSocketConnected => _webSocket?.State == WebSocketState.Open;

        public async Task<long?> CreateRoomAsync(string title, List<long> recipientIds)
        {
            try
            {
                var payload = new { title = title, recipientIds = recipientIds };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/messages/rooms");
                request.Content = content;
                if (token != null && header != null) request.Headers.Add(header, token);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var resJson = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(resJson);
                    return doc.RootElement.GetProperty("room").GetProperty("id").GetInt64();
                }
            }
            catch { }
            return null;
        }

        public async Task<bool> InviteMembersAsync(long roomId, List<long> userIds)
        {
            try
            {
                var payload = new { userIds = userIds };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/messages/{roomId}/invite");
                request.Content = content;
                if (token != null && header != null) request.Headers.Add(header, token);

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<bool> LeaveRoomAsync(long roomId)
        {
            try
            {
                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/messages/{roomId}/leave");
                if (token != null && header != null) request.Headers.Add(header, token);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<MessageRoomModel?> UpdateRoomSettingsAsync(long roomId, string? customTitle, bool pinned, bool muted, int opacityPercent)
        {
            try
            {
                var payload = new { customTitle, pinned, muted, opacityPercent };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/messages/{roomId}/settings");
                request.Content = content;
                if (token != null && header != null) request.Headers.Add(header, token);

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                return MessageRoomModel.FromJson(doc.RootElement);
            }
            catch { return null; }
        }

        /// <summary>세션당 한 번 받아 둔 CSRF 토큰(로그인·로그아웃·세션 만료 때 비운다).</summary>
        private (string? token, string? header) _csrf;

        private void ResetCsrf() => _csrf = (null, null);

        private async Task<(string? token, string? header)> GetDashboardCsrfAsync()
        {
            if (_csrf.token != null && _csrf.header != null) return _csrf;
            try
            {
                // 1.0.28 서버: 가벼운 전용 API
                using var response = await _httpClient.GetAsync($"{_baseUrl}/api/desktop/csrf");
                if (response.IsSuccessStatusCode && !IsLoginPageResponse(response))
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                    var header = doc.RootElement.TryGetProperty("headerName", out var h) ? h.GetString() : null;
                    if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(header)) return _csrf = (token, header);
                }
            }
            catch { }
            try {
                // 구버전 서버 폴백 — 화면 HTML 에서 긁는다
                var html = await _httpClient.GetStringAsync($"{_baseUrl}/dashboard");
                var tokenMatch = Regex.Match(html, @"name=""_csrf""\s+content=""([^""]+)""");
                var headerMatch = Regex.Match(html, @"name=""_csrf_header""\s+content=""([^""]+)""");
                if (tokenMatch.Success && headerMatch.Success)
                    return _csrf = (tokenMatch.Groups[1].Value, headerMatch.Groups[1].Value);

                var inputMatch = Regex.Match(html, @"name=""_csrf""\s+value=""([^""]+)""");
                if (inputMatch.Success) return _csrf = (inputMatch.Groups[1].Value, "X-CSRF-TOKEN");
            } catch { }
            return (null, null);
        }

        public async Task<bool> DeleteNotificationAsync(long id)
        {
            try
            {
                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/notifications/{id}/delete");
                if (token != null && header != null) request.Headers.Add(header, token);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>[W9] 알림 모두 읽음 — 웹 우측 패널과 동일 기능(서버 /api/notifications/read-all).</summary>
        public async Task<bool> MarkAllNotificationsReadAsync()
        {
            try
            {
                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/notifications/read-all");
                if (token != null && header != null) request.Headers.Add(header, token);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<bool> MarkNotificationReadAsync(long id)
        {
            try
            {
                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/notifications/{id}/read");
                if (token != null && header != null) request.Headers.Add(header, token);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>[S-3] 초대 후보 직원 목록 — 초대 모달 열릴 때만 호출.</summary>
        public async Task<List<EmployeeModel>?> GetInviteCandidatesAsync(long roomId)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/messages/{roomId}/invite-candidates");
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
                var list = new List<EmployeeModel>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    long id = 0;
                    if (el.TryGetProperty("id", out var idP)) id = idP.GetInt64();
                    else if (el.TryGetProperty("userId", out var uP)) id = uP.GetInt64();
                    list.Add(new EmployeeModel
                    {
                        UserId = id,
                        Name = el.TryGetProperty("name", out var n) ? n.GetString() : null,
                        JobTitle = el.TryGetProperty("jobTitle", out var j) ? j.GetString() : null,
                        TeamName = el.TryGetProperty("teamName", out var t) ? t.GetString() : null,
                        Email = el.TryGetProperty("email", out var em) ? em.GetString() : null,
                        ProfileImageFilename = el.TryGetProperty("profileImageFilename", out var pf) ? pf.GetString() : null,
                    });
                }
                return list;
            }
            catch { return null; }
        }

        public async Task<bool> MarkRoomNotificationsReadAsync(long roomId)
        {
            try
            {
                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/messages/{roomId}/read-notifications");
                if (token != null && header != null) request.Headers.Add(header, token);
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ----- 업무전달 -----
        public async Task<JsonElement?> GetWorkMemosAsync(string box, string? query = null, int page = 0)
        {
            try
            {
                var qs = new StringBuilder($"box={Uri.EscapeDataString(box)}&page={page}");
                if (!string.IsNullOrWhiteSpace(query)) qs.Append("&q=").Append(Uri.EscapeDataString(query));
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/work-memos?{qs}");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonDocument.Parse(json).RootElement;
                }
            }
            catch { }
            return null;
        }

        public async Task<JsonElement?> GetWorkMemoDetailAsync(long memoId, bool markRead = true)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/work-memos/{memoId}?markRead={markRead.ToString().ToLower()}");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonDocument.Parse(json).RootElement;
                }
            }
            catch { }
            return null;
        }

        public async Task<int> GetUnreadWorkMemoCountAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/work-memos/unread-count");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("count").GetInt32();
                }
            }
            catch { }
            return 0;
        }

        public async Task<long?> CreateWorkMemoAsync(string title, string? body, List<long> recipientIds,
            string importance, IReadOnlyList<string>? filePaths = null,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var r = await CreateWorkMemoExAsync(title, body, recipientIds, importance, filePaths, progress, ct);
            return r.Id;
        }

        public class CreateWorkMemoResult
        {
            public bool Success { get; set; }
            public long? Id { get; set; }
            public string? ErrorMessage { get; set; }
        }

        public async Task<CreateWorkMemoResult> CreateWorkMemoExAsync(string title, string? body, List<long> recipientIds,
            string importance, IReadOnlyList<string>? filePaths = null,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            try
            {
                var content = new MultipartFormDataContent();
                content.Add(new StringContent(title ?? "", Encoding.UTF8), "title");
                if (!string.IsNullOrWhiteSpace(body)) content.Add(new StringContent(body, Encoding.UTF8), "body");
                content.Add(new StringContent(importance ?? "NORMAL", Encoding.UTF8), "importance");
                foreach (var rid in recipientIds) content.Add(new StringContent(rid.ToString()), "recipientIds");
                if (filePaths != null)
                {
                    foreach (var path in filePaths.Where(File.Exists))
                    {
                        var stream = File.OpenRead(path);
                        var fc = new StreamContent(stream);
                        fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GuessContentType(path));
                        content.Add(fc, "files", Path.GetFileName(path));
                    }
                }

                var (token, header) = await GetDashboardCsrfAsync();
                HttpContent payload = progress != null ? new ProgressableContent(content, progress, ct) : content;
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/work-memos") { Content = payload };
                if (token != null && header != null) request.Headers.Add(header, token);

                // [1.0.86] 큰 첨부 대비 — HttpClient 기본 100초 타임아웃 우회용 CTS.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(10));

                var response = await _httpClient.SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cts.Token);
                    var doc = JsonDocument.Parse(json);
                    return new CreateWorkMemoResult { Success = true, Id = doc.RootElement.GetProperty("id").GetInt64() };
                }
                else
                {
                    string detail = await SafeReadBody(response);
                    string msg = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized => "로그인이 만료되었습니다. 다시 로그인해 주세요.",
                        System.Net.HttpStatusCode.Forbidden => "접근 권한이 없습니다.",
                        System.Net.HttpStatusCode.RequestEntityTooLarge => "첨부파일이 너무 큽니다 (총 100MB 한도).",
                        System.Net.HttpStatusCode.BadRequest => $"요청 오류: {detail}",
                        _ => $"서버 응답 {(int)response.StatusCode}: {detail}"
                    };
                    return new CreateWorkMemoResult { ErrorMessage = msg };
                }
            }
            catch (OperationCanceledException)
            {
                return new CreateWorkMemoResult { ErrorMessage = "업로드 시간이 초과됐습니다. 네트워크를 확인하고 다시 시도해 주세요." };
            }
            catch (HttpRequestException ex)
            {
                return new CreateWorkMemoResult { ErrorMessage = $"네트워크 오류: {ex.Message}" };
            }
            catch (Exception ex)
            {
                return new CreateWorkMemoResult { ErrorMessage = $"전송 중 오류: {ex.Message}" };
            }
        }

        private static async Task<string> SafeReadBody(HttpResponseMessage response)
        {
            try
            {
                var s = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(s)) return "";
                if (s.Length > 200) s = s.Substring(0, 200) + "...";
                return s;
            }
            catch { return ""; }
        }

        public async Task<bool> ToggleWorkMemoStarAsync(long memoId)
        {
            try
            {
                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/work-memos/{memoId}/star");
                if (token != null && header != null) request.Headers.Add(header, token);
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("starred").GetBoolean();
                }
            }
            catch { }
            return false;
        }

        public async Task<long?> OpenDirectRoomAsync(long userId)
        {
            try
            {
                var (token, header) = await GetDashboardCsrfAsync();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/messages/direct/{userId}");
                if (token != null && header != null) request.Headers.Add(header, token);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("room").GetProperty("id").GetInt64();
                }
            }
            catch { }
            return null;
        }

        /// <summary>[UX20] 지금 출근(kind=in)/퇴근(out)을 찍으면 어떤 판정인지 — 웹 확인 창과 같은 값. 실패하면 null.</summary>
        public async Task<CheckPreview?> GetCheckPreviewAsync(string kind)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/api/desktop/attendance/preview?kind={Uri.EscapeDataString(kind)}");
                if (!response.IsSuccessStatusCode || IsLoginPageResponse(response)) return null;
                return JsonSerializer.Deserialize<CheckPreview>(await response.Content.ReadAsStringAsync(), _jsonOptions);
            }
            catch
            {
                return null;
            }
        }

        public async Task<AttendanceRecord?> GetTodayAttendanceAsync()
        {
            try
            {
                var json = await _httpClient.GetStringAsync($"{_baseUrl}/api/desktop/attendance/today");
                return JsonSerializer.Deserialize<AttendanceRecord>(json, _jsonOptions);
            }
            catch { return null; }
        }

        public async Task<EmployeeModel?> GetCurrentUserProfileAsync()
        {
            try {
                var json = await _httpClient.GetStringAsync($"{_baseUrl}/api/desktop/me");
                var profile = JsonSerializer.Deserialize<DesktopUserPayload>(json, _jsonOptions);
                if (profile == null) return null;
                return new EmployeeModel {
                    UserId = profile.UserId,
                    Name = profile.Name,
                    TeamName = string.IsNullOrWhiteSpace(profile.TeamName) ? profile.Dept : profile.TeamName,
                    JobTitle = profile.JobTitle,
                    Email = profile.Email,
                    MobileNumber = profile.MobileNumber,
                    ExtensionNumber = profile.ExtensionNumber,
                    ProfileImageFilename = profile.ProfileImageFilename
                };
            } catch { }
            return null;
        }

        /// <summary>[W3] 성공 여부 + 실패 시 서버 사유(BusinessException message). 네트워크 오류는 별도 안내.</summary>
        public async Task<(bool Success, string? Message)> PerformAttendanceAsync(string path)
        {
            try
            {
                var endpoint = path.Contains("check-out")
                    ? "/api/desktop/attendance/check-out"
                    : "/api/desktop/attendance/check-in";
                var response = await PostJsonAsync($"{_baseUrl}{endpoint}", new { });
                if (response.IsSuccessStatusCode) return (true, null);
                string? message = null;
                try
                {
                    var body = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString();
                }
                catch { }
                return (false, message);
            }
            catch
            {
                return (false, "서버에 연결할 수 없습니다. 네트워크 상태를 확인해 주세요.");
            }
        }

        /// <summary>배포시스템 SSO 연동 여부(웹 열기 2단 메뉴에서 배포 항목 노출 판단).</summary>
        public async Task<bool> IsDeploySsoLinkedAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/desktop/integrations");
                if (!response.IsSuccessStatusCode) return false;
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("deploySsoLinked", out var v)
                       && v.ValueKind == JsonValueKind.True;
            }
            catch { return false; }
        }

        /// <summary>앱 시작 OTP: 이 계정에 OTP 필요/등록 여부.</summary>
        public async Task<OtpStateResult?> GetOtpStateAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/desktop/otp/state");
                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var r = doc.RootElement;
                return new OtpStateResult
                {
                    Required = r.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True,
                    Enrolled = r.TryGetProperty("enrolled", out var en) && en.ValueKind == JsonValueKind.True
                };
            }
            catch { return null; }
        }

        /// <summary>OTP 등록용 QR/수동키(미등록 계정).</summary>
        public async Task<OtpSetupResult?> GetOtpSetupAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/desktop/otp/setup");
                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var r = doc.RootElement;
                return new OtpSetupResult
                {
                    Secret = r.TryGetProperty("secret", out var s) ? s.GetString() : null,
                    QrDataUri = r.TryGetProperty("qrDataUri", out var q) ? q.GetString() : null
                };
            }
            catch { return null; }
        }

        public async Task<bool> EnrollOtpAsync(string code) => await OtpPostOkAsync("/api/desktop/otp/enroll", code);
        public async Task<bool> VerifyOtpAsync(string code) => await OtpPostOkAsync("/api/desktop/otp/verify", code);

        private async Task<bool> OtpPostOkAsync(string path, string code)
        {
            try
            {
                var response = await PostJsonAsync($"{_baseUrl}{path}", new { code });
                if (!response.IsSuccessStatusCode) return false;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            }
            catch { return false; }
        }

        public async Task<string?> CreateSsoUrlAsync(string redirect = "/dashboard")
        {
            try
            {
                var response = await PostJsonAsync($"{_baseUrl}/api/desktop/sso-token", new { redirect });
                if (!response.IsSuccessStatusCode) return null;
                var json = await response.Content.ReadAsStringAsync();
                var sso = JsonSerializer.Deserialize<DesktopSsoResponse>(json, _jsonOptions);
                if (string.IsNullOrEmpty(sso?.Url)) return null;
                return sso.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? sso.Url : $"{_baseUrl}{sso.Url}";
            }
            catch { return null; }
        }

        /// <summary>
        /// 연결 세대 — 시작할 때마다 1 올리고, 루프는 자기 세대가 아니면 즉시 끝난다.
        /// 예전엔 중지 직후 재시작하면 대기 중이던 옛 루프가 살아남아 두 루프가 같은 소켓을 나눠 쓰고
        /// 알림·새로고침이 두 번씩 오는 경우가 있었다(분석 B4).
        /// </summary>
        private int _wsGeneration;

        private void StartWebSocket()
        {
            StopWebSocket();
            int generation = Interlocked.Increment(ref _wsGeneration);
            var cts = new CancellationTokenSource();
            _wsCts = cts;
            _reconnectAttempt = 0;
            _ = Task.Run(() => ReconnectLoop(generation, cts.Token));
        }

        private bool IsCurrent(int generation, CancellationToken stop)
            => !stop.IsCancellationRequested && generation == Volatile.Read(ref _wsGeneration);

        private async Task ReconnectLoop(int generation, CancellationToken stop)
        {
            while (IsCurrent(generation, stop))
            {
                var ws = new ClientWebSocket();
                try
                {
                    var uri = new Uri(_baseUrl);
                    var cookies = _handler.CookieContainer.GetCookies(uri);
                    // 서버 세션 쿠키명은 OFFICESESSION 으로 분리됨(배포 시스템 DEPLOYOPSSESSION 과 충돌 방지).
                    // 구버전 서버 호환을 위해 JSESSIONID 도 폴백으로 시도한다.
                    var sessionCookie = cookies["OFFICESESSION"] ?? cookies["JSESSIONID"];
                    if (sessionCookie != null)
                    {
                        ws.Options.SetRequestHeader("Cookie", $"{sessionCookie.Name}={sessionCookie.Value}");
                    }
                    await ws.ConnectAsync(new Uri(_wsUrl), stop);
                    if (!IsCurrent(generation, stop)) break;
                    _webSocket = ws;
                    _reconnectAttempt = 0;
                    OnWebSocketConnected?.Invoke();
                    await ReceiveLoop(ws, generation, stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // [진단] 웹소켓이 죽으면 실시간 알림이 오지 않는다 — 현장 민원 1순위인데
                    //  예전에는 조용히 재시도만 해서 "알림이 안 와요" 의 원인을 확인할 수 없었다.
                    //  재연결 횟수를 같이 남겨, 한 번 끊긴 것인지 계속 실패 중인지 구분한다.
                    AppLog.Warn("ws", $"connect/receive failed (attempt={_reconnectAttempt + 1}): {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_webSocket, ws)) _webSocket = null;
                    try { ws.Dispose(); } catch { }
                }

                if (!IsCurrent(generation, stop)) break;

                _reconnectAttempt++;
                // [S-8] 첫 끊김 직후엔 즉시(200ms) 재시도, 이후 지수 백오프 1→2→4→8→16→30초 (한계 30초)
                int delayMs = _reconnectAttempt == 1
                    ? 200
                    : (int) Math.Min(30000, 1000 * Math.Pow(2, Math.Min(_reconnectAttempt - 2, 5)));
                try { await Task.Delay(delayMs, stop); } catch (OperationCanceledException) { break; }
            }
        }

        private void StopWebSocket()
        {
            // Stop reconnect loop and gracefully close to fire PRESENCE_CHANGED immediately.
            // [W4] 종전에는 CloseAsync().GetResult()로 UI 스레드를 최대 2초 블로킹(로그아웃/종료 멈칫) —
            // 종료는 백그라운드에서 진행하고 호출부는 즉시 반환한다.
            Interlocked.Increment(ref _wsGeneration); // 대기 중인 루프도 다음 확인에서 끝난다
            var ws = _webSocket;
            _webSocket = null;
            var cts = _wsCts;
            _wsCts = null;
            try { cts?.Cancel(); } catch { }
            _ = Task.Run(async () =>
            {
                if (ws != null)
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client_close", closeCts.Token);
                        }
                    }
                    catch { }
                    try { ws.Dispose(); } catch { }
                }
                try { cts?.Dispose(); } catch { }
            });
        }

        private async Task ReceiveLoop(ClientWebSocket ws, int generation, CancellationToken stop)
        {
            // [W1] 16KB 초과 프레임 대응 — EndOfMessage까지 조립해야 긴 메시지가 유실되지 않는다.
            var buffer = new byte[1024 * 16];
            using var messageStream = new System.IO.MemoryStream();
            while (ws.State == WebSocketState.Open && IsCurrent(generation, stop))
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stop);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }
                    var msg = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                    messageStream.SetLength(0);
                    if (!IsCurrent(generation, stop)) break; // 중지된 세대의 메시지는 버린다
                    await ProcessWsMessage(msg);
                }
                catch { break; }
            }
        }

        private async Task ProcessWsMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? "";
                    if (type == "KICKED")
                    {
                        string reason = "다른 PC에서 로그인되어 세션이 종료되었습니다.";
                        if (root.TryGetProperty("payload", out var kp)
                            && kp.TryGetProperty("reasonText", out var reasonProp))
                        {
                            reason = reasonProp.GetString() ?? reason;
                        }
                        OnKicked?.Invoke(reason);
                        return;
                    }
                    if (type == "PRESENCE_CHANGED" || type == "CONNECTED")
                    {
                        if (root.TryGetProperty("payload", out var presencePayload)
                            && presencePayload.TryGetProperty("onlineUserIds", out var idsProp)
                            && idsProp.ValueKind == JsonValueKind.Array)
                        {
                            var ids = idsProp.EnumerateArray().Select(e => e.GetInt64()).ToList();
                            var awayIds = presencePayload.TryGetProperty("awayUserIds", out var awayProp)
                                && awayProp.ValueKind == JsonValueKind.Array
                                ? awayProp.EnumerateArray().Select(e => e.GetInt64()).ToList()
                                : new List<long>();
                            OnPresenceChanged?.Invoke(ids, awayIds);
                        }
                        return;
                    }
                    if (type == "PROFILE_CHANGED")
                    {
                        if (root.TryGetProperty("payload", out var pp))
                        {
                            long uid = pp.TryGetProperty("userId", out var u) ? u.GetInt64() : 0;
                            string fn = pp.TryGetProperty("profileImageFilename", out var f) ? (f.GetString() ?? "") : "";
                            if (uid > 0) OnProfileChanged?.Invoke(uid, fn);
                        }
                        return;
                    }
                    if (type == "MESSAGE_CREATED")
                    {
                        if (root.TryGetProperty("payload", out var createdPayload)
                            && createdPayload.TryGetProperty("roomId", out var createdRoomProp))
                        {
                            try
                            {
                                OnMessageCreated?.Invoke(createdPayload.Clone());
                            }
                            catch { }
                        }
                        return;
                    }
                    if (type == "MESSAGE_CHANGED")
                    {
                        long roomId = 0;
                        if (root.TryGetProperty("payload", out var mp) && mp.TryGetProperty("roomId", out var rp))
                        {
                            try { roomId = rp.GetInt64(); } catch { }
                        }
                        OnMessageChanged?.Invoke(roomId);
                    }
                    if (type == "PANEL_CHANGED" || type == "MESSAGE_CHANGED" || type == "NOTIFICATION")
                    {
                        // [W2] 이벤트마다 전체 재조회하던 것을 250ms 코얼레싱 — 버스트 시 HTTP 1회로 병합.
                        SchedulePanelRefresh();
                    }
                }
            }
            catch { }
        }

        /* ── [W2] 우측 패널 재조회 디바운스(코얼레싱) ── */
        private int _panelRefreshScheduled;
        private DateTime _lastPanelTriggerUtc;

        private void SchedulePanelRefresh()
        {
            _lastPanelTriggerUtc = DateTime.UtcNow;
            if (System.Threading.Interlocked.CompareExchange(ref _panelRefreshScheduled, 1, 0) != 0)
            {
                return; // 이미 예약됨 — 마지막 트리거 시각만 갱신
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    // 마지막 트리거 후 250ms 조용해질 때까지 대기
                    while (true)
                    {
                        var remain = TimeSpan.FromMilliseconds(250) - (DateTime.UtcNow - _lastPanelTriggerUtc);
                        if (remain <= TimeSpan.Zero) break;
                        await Task.Delay(remain);
                    }
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _panelRefreshScheduled, 0);
                }
                await RefreshPanelSnapshotAsync();
            });
        }

        private async Task RefreshPanelSnapshotAsync()
        {
            try
            {
                var data = await GetRightPanelDataAsync();
                if (data.HasValue)
                {
                    OnPanelDataSnapshotUpdated?.Invoke(data.Value.Clone());

                    if (data.Value.TryGetProperty("notifications", out var ns))
                    {
                        var newNotifications = ns.EnumerateArray()
                            .Select(e => new { Element = e, Id = e.GetProperty("id").GetInt64() })
                            .Where(x => _lastNotificationId >= 0 && x.Id > _lastNotificationId)
                            .OrderBy(x => x.Id)
                            .ToList();

                        if (newNotifications.Count > 0)
                        {
                            _lastNotificationId = newNotifications.Max(x => x.Id);
                            foreach (var item in newNotifications)
                            {
                                var e = item.Element;
                                string notiType = e.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "" : "";
                                string prefix = notiType switch {
                                    "DEPLOY_APPROVAL_REQUESTED" => "[배포 승인] ",
                                    "DEPLOY_INCIDENT" => "[배포 사고] ",
                                    "DEPLOY_FAILED" => "[배포 실패] ",
                                    "ROLLBACK_COMPLETED" => "[배포 롤백] ",
                                    "NOTICE" => "[공지] ",
                                    "MESSAGE" => "[새 메세지] ",
                                    "APPROVAL" => "[결재] ",
                                    "WORK_MEMO" => "[자료전송] ",
                                    _ => ""
                                };

                                var notification = new NotificationModel
                                {
                                    Id = item.Id,
                                    Type = notiType,
                                    Title = prefix + (e.TryGetProperty("title", out var titleP) ? titleP.GetString() : "WorkHub Desktop"),
                                    Body = e.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : "",
                                    PlainBody = e.TryGetProperty("plainBody", out var p) ? p.GetString() : e.GetProperty("body").GetString(),
                                    LinkUrl = e.TryGetProperty("linkUrl", out var linkProp) ? linkProp.GetString() : null,
                                    ReadAt = e.TryGetProperty("readAt", out var readProp) ? readProp.GetString() : null
                                };
                                OnNotificationReceived?.Invoke(notification);
                            }
                        }
                        else if (_lastNotificationId < 0)
                        {
                            // 로그인 직후에도 로그아웃 중 누적된 배포 사고/승인 알림은 토스트로 전달한다.
                            foreach (var e in ns.EnumerateArray()
                                         .Where(x => x.TryGetProperty("type", out var t)
                                                     && (t.GetString() ?? "").StartsWith("DEPLOY_", StringComparison.OrdinalIgnoreCase)
                                                     && (!x.TryGetProperty("readAt", out var r) || r.ValueKind == JsonValueKind.Null)))
                            {
                                OnNotificationReceived?.Invoke(new NotificationModel
                                {
                                    Id = e.GetProperty("id").GetInt64(),
                                    Type = e.TryGetProperty("type", out var t) ? t.GetString() : null,
                                    Title = e.TryGetProperty("title", out var title) ? title.GetString() : "배포 알림",
                                    Body = e.TryGetProperty("body", out var body) ? body.GetString() : "",
                                    PlainBody = e.TryGetProperty("plainBody", out var plain) ? plain.GetString() : null,
                                    LinkUrl = e.TryGetProperty("linkUrl", out var link) ? link.GetString() : "/notifications",
                                    ReadAt = null
                                });
                            }
                            var latest = ns.EnumerateArray().FirstOrDefault();
                            if (latest.ValueKind != JsonValueKind.Null && latest.ValueKind != JsonValueKind.Undefined)
                                _lastNotificationId = latest.GetProperty("id").GetInt64();
                        }
                    }
                }
                else
                {
                    OnPanelDataUpdated?.Invoke();
                }
            }
            catch { }
        }

        public void Dispose()
        {
            StopWebSocket();
            _httpClient.Dispose();
            _handler.Dispose();
        }
    }

    /// <summary>[UX20] 출퇴근 판정 미리보기 — allowed=false 면 label 이 막힌 사유.</summary>
    public class CheckPreview
    {
        public bool Allowed { get; set; }
        public string? Label { get; set; }
        public string? NowTime { get; set; }
    }

    public class AttendanceRecord
    {
        public bool CheckedIn { get; set; }
        public bool CheckedOut { get; set; }
        public string? CheckInAt { get; set; }
        public string? CheckOutAt { get; set; }
        public string? Status { get; set; }
        public string? StatusLabel { get; set; }
        /// <summary>[W7] 오늘 기준 근무시간 — 요일별 근무시간표·시차출근 반영("09:00~13:00 (금요일 반일)").</summary>
        public string? ScheduleText { get; set; }
        /// <summary>[W8] 오늘 출근 체크 불가 사유(휴무일·휴직·승인 출장/휴가 등) — null이면 체크 가능. 웹과 동일 규칙(서버 응답).</summary>
        public string? BlockReason { get; set; }
        /// <summary>[OT] 지금 출근을 누르면 시간외 기록이 되는 사유(휴무일·기준 퇴근시각 이후·퇴근 후 재출근) — 정규 출근이면 null.</summary>
        public string? OvertimeReason { get; set; }
        /// <summary>[OT] 아직 퇴근을 찍지 않은 시간외 기록의 출근 시각 — 없으면 null.</summary>
        public string? OvertimeCheckInAt { get; set; }
        /// <summary>[OT] 이번 달 신청하지 않은 시간외 근무 건수(서버 1.0.28 응답부터).</summary>
        public int UnrequestedOvertimeCount { get; set; }
        public bool IsOvertimeOpen => !string.IsNullOrEmpty(OvertimeCheckInAt);
        public bool IsOvertimeMode => string.IsNullOrWhiteSpace(BlockReason) && !string.IsNullOrWhiteSpace(OvertimeReason);
        public string? CheckInAtLabel { get; set; }
        public string? CheckOutAtLabel { get; set; }
        public bool IsCheckedIn => CheckedIn || !string.IsNullOrEmpty(CheckInAtLabel) || !string.IsNullOrEmpty(CheckInAt);
        public bool IsCheckedOut => CheckedOut || !string.IsNullOrEmpty(CheckOutAtLabel) || !string.IsNullOrEmpty(CheckOutAt);
        public string? DisplayCheckIn => CheckInAtLabel ?? CheckInAt;
        public string? DisplayCheckOut => CheckOutAtLabel ?? CheckOutAt;
    }

    public class DesktopAuthResponse
    {
        public bool Authenticated { get; set; }
        public string? Token { get; set; }
        public DesktopUserPayload? User { get; set; }
    }

    public class DesktopUserPayload
    {
        public bool MustChangePassword { get; set; }
        public long UserId { get; set; }
        public string? Username { get; set; }
        public string? Name { get; set; }
        public string? TeamName { get; set; }
        public string? Dept { get; set; }
        public string? JobTitle { get; set; }
        public string? Email { get; set; }
        public string? MobileNumber { get; set; }
        public string? ExtensionNumber { get; set; }
        public string? ProfileImageFilename { get; set; }
    }

    public class DesktopSsoResponse
    {
        public string? Url { get; set; }
        public string? ExpiresAt { get; set; }
    }

    public class OtpStateResult
    {
        public bool Required { get; set; }
        public bool Enrolled { get; set; }
    }

    public class OtpSetupResult
    {
        public string? Secret { get; set; }
        public string? QrDataUri { get; set; }
    }

    public class NotificationModel
    {
        public long Id { get; set; }
        public string? SenderName { get; set; }
        public string? Type { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? PlainBody { get; set; }
        public string? CreatedAtLabel { get; set; }
        public string? LinkUrl { get; set; }
        public string? ReadAt { get; set; }
        public bool IsUnread => string.IsNullOrEmpty(ReadAt);
    }

    public class NotificationGroup : System.ComponentModel.INotifyPropertyChanged
    {
        public string? LinkUrl { get; set; }
        public string? SectionTitle { get; set; }
        public List<NotificationModel> Items { get; set; } = new();
        public NotificationModel Head => Items[0];
        public int Count => Items.Count;
        public bool IsGroup => Items.Count > 1;
        public System.Windows.Visibility SectionVisibility => string.IsNullOrWhiteSpace(SectionTitle) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        public System.Windows.Visibility GroupBadgeVisibility => IsGroup ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public string CountLabel => "+" + (Count - 1);

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); OnPropertyChanged(nameof(ExpandedVisibility)); OnPropertyChanged(nameof(ChevronRotation)); } }
        }
        public System.Windows.Visibility ExpandedVisibility => (IsGroup && _isExpanded) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public double ChevronRotation => _isExpanded ? 180 : 0;
        public List<NotificationModel> Tail => Items.Skip(1).ToList();

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string p) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
    }

    public class MessageRoomModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        private string? _title;
        private int _unreadCount;
        private string? _lastMessageBody;
        private bool _pinned;
        private bool _muted;
        private int _opacityPercent = 100;
        private List<RoomAvatarTile> _avatarTiles = new();

        public long Id { get; set; }
        public string? Title { get => _title; set { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(TitleInitial)); } }
        public string? TitleInitial => string.IsNullOrEmpty(Title) ? "?" : Title.Substring(0, 1);
        public int UnreadCount { get => _unreadCount; set { _unreadCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUnread)); } }
        public bool HasUnread => UnreadCount > 0;
        public string? LastMessageBody { get => _lastMessageBody; set { _lastMessageBody = value; OnPropertyChanged(); } }
        public string? CustomTitle { get; set; }
        public bool Pinned { get => _pinned; set { _pinned = value; OnPropertyChanged(); OnPropertyChanged(nameof(PinnedVisibility)); OnPropertyChanged(nameof(PinActionText)); } }
        public bool Muted { get => _muted; set { _muted = value; OnPropertyChanged(); OnPropertyChanged(nameof(MutedVisibility)); OnPropertyChanged(nameof(MuteActionText)); } }
        public int OpacityPercent { get => _opacityPercent; set { _opacityPercent = Math.Clamp(value, 0, 100); OnPropertyChanged(); } }
        public List<RoomAvatarTile> AvatarTiles { get => _avatarTiles; set { _avatarTiles = value; OnPropertyChanged(); } }
        public System.Windows.Visibility PinnedVisibility => Pinned ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility MutedVisibility => Muted ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public string PinActionText => Pinned ? "채팅방 상단 고정 해제" : "채팅방 상단 고정";
        public string MuteActionText => Muted ? "채팅방 알림 켜기" : "채팅방 알림 끄기";

        public static MessageRoomModel FromJson(JsonElement x)
        {
            var model = new MessageRoomModel
            {
                Id = x.GetProperty("id").GetInt64(),
                Title = x.TryGetProperty("title", out var t) ? t.GetString() : null,
                CustomTitle = x.TryGetProperty("customTitle", out var ct) && ct.ValueKind != JsonValueKind.Null ? ct.GetString() : null,
                LastMessageBody = x.TryGetProperty("lastMessageBody", out var b) && b.ValueKind != JsonValueKind.Null ? b.GetString() : "",
                UnreadCount = x.TryGetProperty("unreadCount", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0,
                Pinned = x.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.True,
                Muted = x.TryGetProperty("muted", out var m) && m.ValueKind == JsonValueKind.True,
                OpacityPercent = x.TryGetProperty("opacityPercent", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 100
            };
            model.AvatarTiles = RoomAvatarTile.FromJson(x.TryGetProperty("avatarMembers", out var avatars) ? avatars : default);
            return model;
        }

        public void Apply(MessageRoomModel updated)
        {
            Title = updated.Title;
            CustomTitle = updated.CustomTitle;
            LastMessageBody = updated.LastMessageBody;
            UnreadCount = updated.UnreadCount;
            Pinned = updated.Pinned;
            Muted = updated.Muted;
            OpacityPercent = updated.OpacityPercent;
            AvatarTiles = updated.AvatarTiles;
        }
    }

    public class RoomAvatarTile
    {
        public string? Name { get; set; }
        public string? ProfileImageFilename { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int RowSpan { get; set; } = 1;
        public int ColumnSpan { get; set; } = 1;
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim().Substring(0, 1);
        public string ProfileImageUrl => !string.IsNullOrEmpty(ProfileImageFilename)
            ? $"{EmployeeModel.GetBaseUrl()}/profile-images/{ProfileImageFilename}"
            : "about:blank";
        public bool HasProfileImage => !string.IsNullOrEmpty(ProfileImageFilename);

        public static List<RoomAvatarTile> FromJson(JsonElement avatars)
        {
            var members = new List<RoomAvatarTile>();
            if (avatars.ValueKind != JsonValueKind.Array) return members;
            foreach (var item in avatars.EnumerateArray().Take(4))
            {
                members.Add(new RoomAvatarTile
                {
                    Name = item.TryGetProperty("userName", out var name) ? name.GetString() : null,
                    ProfileImageFilename = item.TryGetProperty("profileImageFilename", out var image) && image.ValueKind != JsonValueKind.Null ? image.GetString() : null
                });
            }
            if (members.Count == 1)
            {
                members[0].RowSpan = 2;
                members[0].ColumnSpan = 2;
            }
            for (int i = 0; i < members.Count; i++)
            {
                if (members.Count == 2)
                {
                    members[i].Row = 0;
                    members[i].Column = i;
                    members[i].RowSpan = 2;
                }
                else if (members.Count > 2)
                {
                    members[i].Row = i / 2;
                    members[i].Column = i % 2;
                }
            }
            return members;
        }
    }

    public class NotificationSection
    {
        public string Title { get; set; } = "";
        public List<NotificationGroup> Groups { get; set; } = new();
        public int Count => Groups.Sum(g => g.Items.Count);
        public System.Windows.Visibility Visibility => Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public class DesktopDocumentModel
    {
        public string? DocumentType { get; set; }
        public string? DocumentTypeLabel { get; set; }
        public long DocumentId { get; set; }
        public string? RequesterName { get; set; }
        public string? Title { get; set; }
        public string? CreatedAt { get; set; }
        public string? DocumentUrl { get; set; }
        public string? Box { get; set; }
        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "-" : Title!;
        public string DisplayRequester => string.IsNullOrWhiteSpace(RequesterName) ? "-" : RequesterName!;
        public string DisplayType => string.IsNullOrWhiteSpace(DocumentTypeLabel) ? "문서" : DocumentTypeLabel!;
        public string CreatedAtLabel => DateTime.TryParse(CreatedAt, out var dt) ? dt.ToString("yyyy.MM.dd") : "-";
    }

    public class CompanyScheduleModel
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? StartAt { get; set; }
        public string? EndAt { get; set; }
        public string? Color { get; set; }
        public string? CreatedByName { get; set; }
        public bool Holiday { get; set; }

        public DateTime StartDateTime => ParseDateTime(StartAt);
        public DateTime EndDateTime => ParseDateTime(EndAt);
        public bool IsHoliday => Holiday;
        public string TitleText => string.IsNullOrWhiteSpace(Title) ? (IsHoliday ? "휴무" : "일정") : Title!;
        public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? (IsHoliday ? "회사 휴무일" : "상세 내용이 없습니다.") : Description!;
        public string CreatedByText => string.IsNullOrWhiteSpace(CreatedByName) ? "-" : CreatedByName!;
        public string ColorValue => IsHoliday ? "#EF4444" : (string.IsNullOrWhiteSpace(Color) ? "#2563EB" : Color!);
        public string PeriodLabel => IsHoliday ? $"{StartDateTime:MM.dd} 휴무" : $"{StartDateTime:MM.dd HH:mm} ~ {EndDateTime:MM.dd HH:mm}";

        public bool Includes(DateTime date)
        {
            var day = date.Date;
            return StartDateTime.Date <= day && EndDateTime.Date >= day;
        }

        private static DateTime ParseDateTime(string? value)
            => DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;
    }

    public class CalendarDayModel
    {
        public DateTime Date { get; set; }
        public bool InMonth { get; set; }
        public bool IsToday { get; set; }
        public bool IsSelected { get; set; }
        public List<CompanyScheduleModel> Schedules { get; set; } = new();
        public string DayText => Date.Day.ToString();
        public string ScheduleCountLabel => Schedules.Count > 0 ? $"{Schedules.Count}개" : "";
        public bool HasSchedules => Schedules.Count > 0;
        public IEnumerable<string> ScheduleDotBrushes => Schedules
            .OrderByDescending(schedule => schedule.IsHoliday)
            .Take(6)
            .Select(schedule => schedule.ColorValue);
        public string ScheduleCountToolTip
        {
            get
            {
                int holidays = Schedules.Count(schedule => schedule.IsHoliday);
                int schedules = Schedules.Count - holidays;
                if (holidays > 0 && schedules > 0) return $"휴무 {holidays}개 · 일정 {schedules}개";
                if (holidays > 0) return $"휴무 {holidays}개";
                return schedules > 0 ? $"일정 {schedules}개" : "";
            }
        }
        public IEnumerable<CompanyScheduleModel> PreviewSchedules => Schedules.Take(2);
    }

    // 멀티파트 업로드 진행률을 보고하는 HttpContent 래퍼
    internal sealed class ProgressableContent : System.Net.Http.HttpContent
    {
        private readonly System.Net.Http.HttpContent _inner;
        private readonly IProgress<double> _progress;
        private readonly CancellationToken _ct;

        public ProgressableContent(System.Net.Http.HttpContent inner, IProgress<double> progress, CancellationToken ct)
        {
            _inner = inner;
            _progress = progress;
            _ct = ct;
            foreach (var h in inner.Headers)
            {
                Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        protected override async System.Threading.Tasks.Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
        {
            long total = Headers.ContentLength ?? -1L;
            using var src = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[81920];
            long sent = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, _ct).ConfigureAwait(false)) > 0)
            {
                _ct.ThrowIfCancellationRequested();
                await stream.WriteAsync(buffer, 0, read, _ct).ConfigureAwait(false);
                sent += read;
                if (total > 0) _progress.Report(Math.Min(100.0, sent * 100.0 / total));
            }
            _progress.Report(100.0);
        }

        protected override bool TryComputeLength(out long length)
        {
            var len = _inner.Headers.ContentLength;
            length = len ?? -1L;
            return len.HasValue;
        }
    }

    public class MessageModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        private string? _createdAtLabel;
        private bool _isPending;
        private bool _isFailed;
        private bool _isUploading;
        private double _uploadPercent;
        private string? _uploadInfo;

        public long Id { get; set; }
        public long? SenderUserId { get; set; }
        public string? SenderName { get; set; }
        public string? SenderProfileImageFilename { get; set; }
        public string? Body { get; set; }
        public List<MessageAttachmentModel> Attachments { get; set; } = new();
        public bool HasAttachments => Attachments.Count > 0;
        public bool HasBody => !string.IsNullOrWhiteSpace(Body);
        public List<MessageAttachmentModel> ImageAttachments => Attachments.Where(a => a.IsImage).ToList();
        public List<MessageAttachmentModel> FileAttachments => Attachments.Where(a => !a.IsImage).ToList();
        public bool HasImageAttachments => ImageAttachments.Count > 0;
        public bool HasSingleImage => ImageAttachments.Count == 1;
        public bool HasMultipleImages => ImageAttachments.Count > 1;
        public MessageAttachmentModel? SingleImage => ImageAttachments.FirstOrDefault();
        public bool HasFileAttachments => FileAttachments.Count > 0;
        public bool IsImageOnlyMessage => string.IsNullOrWhiteSpace(Body) && HasImageAttachments && !HasFileAttachments;
        public List<MessageAttachmentModel> VisibleImageAttachments
        {
            get
            {
                var images = ImageAttachments;
                var hidden = Math.Max(0, images.Count - 4);
                var visible = images.Take(4).Select(MessageAttachmentModel.CopyFrom).ToList();
                if (hidden > 0 && visible.Count > 0)
                {
                    visible[^1].MoreCount = hidden;
                }
                return visible;
            }
        }
        public DateTime? CreatedAt { get; set; }
        public string? CreatedAtLabel { get => _createdAtLabel; set { _createdAtLabel = value; OnPropertyChanged(); } }
        public bool IsMine { get; set; }
        public bool IsSystem { get; set; }
        public bool IsPending { get => _isPending; set { _isPending = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeVisibility)); OnPropertyChanged(nameof(SpinnerVisibility)); } }
        public bool IsFailed { get => _isFailed; set { _isFailed = value; OnPropertyChanged(); } }
        public System.Windows.Visibility TimeVisibility => (_isPending || _isUploading) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        public System.Windows.Visibility SpinnerVisibility => (_isPending || _isUploading) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public bool IsUploading { get => _isUploading; set { _isUploading = value; OnPropertyChanged(); OnPropertyChanged(nameof(UploadVisibility)); OnPropertyChanged(nameof(TimeVisibility)); OnPropertyChanged(nameof(SpinnerVisibility)); } }
        public double UploadPercent { get => _uploadPercent; set { _uploadPercent = value; OnPropertyChanged(); } }
        public string? UploadInfo { get => _uploadInfo; set { _uploadInfo = value; OnPropertyChanged(); } }
        public System.Windows.Visibility UploadVisibility => _isUploading ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public string? TempKey { get; set; } // optimistic tracking
        // 업로드 시작 시점의 '내 첨부 메시지 수' 기준값. 새로고침에서 이 값보다 늘면 업로드가 서버에 도착한 것으로 보고
        // 업로드 진행 placeholder를 제거한다(이미지가 떴는데 '보내는 중' 로딩이 남는 현상 방지).
        public int UploadBaselineCount { get; set; }
        public string SenderProfileImageUrl => !string.IsNullOrEmpty(SenderProfileImageFilename)
            ? $"{EmployeeModel.GetBaseUrl()}/profile-images/{SenderProfileImageFilename}"
            : "about:blank";
        public bool HasProfileImage => !string.IsNullOrEmpty(SenderProfileImageFilename);

        public static MessageModel DateSeparator(DateTime date)
        {
            var days = new[] { "일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일" };
            return new MessageModel
            {
                Id = -date.Date.Ticks,
                Body = $"{date:yyyy년 M월 d일} {days[(int)date.DayOfWeek]}",
                IsSystem = true
            };
        }
    }

    public class MessageAttachmentModel
    {
        public long Id { get; set; }
        public string? OriginalFilename { get; set; }
        public string? ContentType { get; set; }
        public long FileSize { get; set; }
        public string? AttachmentType { get; set; }
        public string? ExpiresAt { get; set; }
        public int MoreCount { get; set; }
        public string DownloadUrl => $"{EmployeeModel.GetBaseUrl()}/api/messages/attachments/{Id}";
        // 채팅 말풍선은 원본 그대로 — 사용자가 화질 저하를 원치 않음. (그리드 cover 크롭만 적용)
        public string ThumbnailUrl => DownloadUrl;
        public bool IsImage => string.Equals(AttachmentType, "IMAGE", StringComparison.OrdinalIgnoreCase);
        public bool HasMoreCount => MoreCount > 0;
        public string SizeLabel => FileSize >= 1024 * 1024
            ? $"{FileSize / 1024d / 1024d:0.#}MB"
            : $"{Math.Max(1, FileSize / 1024)}KB";
        // 웹 첨부 카드와 동일: 유효기간 + 용량
        public string InfoLabel
        {
            get
            {
                var exp = string.IsNullOrWhiteSpace(ExpiresAt)
                    ? null
                    : (ExpiresAt!.Length >= 10 ? ExpiresAt!.Substring(0, 10).Replace("-", ".") : ExpiresAt);
                return exp != null ? $"유효기간 {exp} · {SizeLabel}" : SizeLabel;
            }
        }

        public static MessageAttachmentModel CopyFrom(MessageAttachmentModel source) => new()
        {
            Id = source.Id,
            OriginalFilename = source.OriginalFilename,
            ContentType = source.ContentType,
            FileSize = source.FileSize,
            AttachmentType = source.AttachmentType,
            ExpiresAt = source.ExpiresAt
        };
    }

    public class EmployeeModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        private bool _online;
        private string? _statusLabel;
        private string? _statusClass;
        private bool _isSelected;
        private string? _profileImageFilename;

        public long UserId { get; set; }
        public string? Name { get; set; }
        public string? TeamName { get; set; }
        public string? JobTitle { get; set; }
        public string? Email { get; set; }
        public string? MobileNumber { get; set; }
        public string? ExtensionNumber { get; set; }
        public string? OrgRole { get; set; }
        // [O-11] 사진 파일명 변경 시 ProfileImageUrl/HasProfileImage 변경 알림 → Image.Source 자동 갱신
        public string? ProfileImageFilename
        {
            get => _profileImageFilename;
            set
            {
                if (_profileImageFilename == value) return;
                _profileImageFilename = value;
                _profileImageUrl = null;   // 파일명 바뀔 때만 URL(cb) 재생성 → 캐시 무효화
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProfileImageUrl));
                OnPropertyChanged(nameof(HasProfileImage));
            }
        }

        public string? StatusLabel { get => _statusLabel; set { _statusLabel = value; OnPropertyChanged(); } }
        public string? StatusClass { get => _statusClass; set { _statusClass = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActualStatusClass)); } }
        public bool Online { get => _online; set { _online = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActualStatusClass)); } }
        private bool _away;
        /// <summary>[PR] 본인이 고른 '자리비움'(접속 중일 때만 의미).</summary>
        public bool Away { get => _away; set { _away = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActualStatusClass)); } }
        public bool Contractor { get; set; }
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

        // 직원 검색 필터(이름/온오프)를 Visibility로 적용 — 트리 재빌드 없이.
        private bool _isVisible = true;
        public bool IsVisible { get => _isVisible; set { if (_isVisible == value) return; _isVisible = value; OnPropertyChanged(); } }

        // [성능] cb(캐시버스터)는 '파일명이 바뀔 때만' 1회 생성해 캐시한다.
        // 과거엔 getter가 호출될 때마다 cb를 새로 만들어, 매 렌더(필터/스크롤/리바인드)마다
        // URL이 달라져 WebImage 캐시 미스 → 아바타를 매번 재다운로드(칩 변경 시 렉의 원인)했다.
        private string? _profileImageUrl;
        public string ProfileImageUrl => _profileImageUrl ??= (!string.IsNullOrEmpty(ProfileImageFilename)
            ? $"{_apiServiceBaseUrl}/profile-images/{ProfileImageFilename}?cb={DateTime.UtcNow.Ticks}"
            : "about:blank");
        public bool HasProfileImage => !string.IsNullOrEmpty(ProfileImageFilename);

        // 아바타 점 = 접속 상태만(웹과 같음): 온라인 초록 / 자리비움 노랑 / 오프라인 빨강. 근태는 옆 글자로 보인다.
        public string ActualStatusClass => Online ? (Away ? "away" : "work") : "off";

        private static string _apiServiceBaseUrl = AppDefaults.ServerUrl;
        public static void UpdateBaseUrl(string url)
        {
            // settings.json에 끝 '/'가 있는 케이스 방어 — 슬래시 두 개로 조립되어 404가 되지 않게.
            _apiServiceBaseUrl = AppDefaults.NormalizeServerUrl(url);
        }
        public static string GetBaseUrl() => _apiServiceBaseUrl;
    }

    public class OrgNodeModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

        public string? Name { get; set; }
        public string? NodeType { get; set; }
        public List<OrgNodeModel> Children { get; set; } = new();
        public List<EmployeeModel> Members { get; set; } = new();

        private bool _isExpanded;
        // [성능] INPC로 바꿔, 펼침/필터 시 ItemsSource 재할당(트리 재빌드) 없이 UI가 갱신되게 한다.
        public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; Raise(nameof(IsExpanded)); } }

        private bool _isVisible = true;
        // 검색/온오프 필터를 트리 재빌드 대신 Visibility 토글로 적용(노드 단위).
        public bool IsVisible { get => _isVisible; set { if (_isVisible == value) return; _isVisible = value; Raise(nameof(IsVisible)); } }
    }

    public class LoginResult
    {
        public bool Success { get; private init; }
        public bool HasConflict { get; private init; }
        /// <summary>[W3] 자격증명 오류가 아니라 서버 연결 실패 — 별도 안내용.</summary>
        public bool NetworkError { get; private init; }
        public string? ExistingDevice { get; private init; }

        public static LoginResult Ok() => new() { Success = true };
        public static LoginResult Failed() => new();
        public static LoginResult Network() => new() { NetworkError = true };
        public static LoginResult Conflict(string existingDevice) => new()
        {
            HasConflict = true,
            ExistingDevice = existingDevice
        };
    }

    public class SystemThemeModel
    {
        public string Brand { get; set; } = "#4F46E5";
        public string BrandDark { get; set; } = "#4338CA";
        public string BrandDeeper { get; set; } = "#3730A3";
        public string BrandLight { get; set; } = "#EEF2FF";
        public string BrandLight2 { get; set; } = "#E0E7FF";
        public string BrandMuted { get; set; } = "#A5B4FC";
    }

    public class SystemBrandingModel
    {
        public string? WebLogoUrl { get; set; }
        public string? DesktopLogoUrl { get; set; }
    }

    public class WorkMemoModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

        public long Id { get; set; }
        public long SenderUserId { get; set; }
        public string? SenderName { get; set; }
        public string? SenderProfileImageFilename { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? Importance { get; set; }
        public DateTime? SentAt { get; set; }
        public string SentAtLabel => SentAt?.ToString("yyyy.MM.dd HH:mm") ?? "";
        public string SentAtShortLabel => SentAt?.ToString("MM.dd") ?? "";
        private bool _starred;
        public bool IsStarred { get => _starred; set { _starred = value; OnPropertyChanged(); OnPropertyChanged(nameof(StarVisibility)); } }
        public DateTime? ReadAt { get; set; }
        // 발신 본인 항목은 read_at 이 null 이라도 NEW로 보이지 않음 — 본인이 보낸 글이 'NEW'로 뜨는 건 부자연.
        public bool IsSenderSelf { get; set; }
        public bool IsUnread => ReadAt == null && !IsSenderSelf;
        public bool IsImportant => string.Equals(Importance, "IMPORTANT", StringComparison.OrdinalIgnoreCase); // [1.0.78] UI 미사용 — 호환용
        public System.Windows.Visibility StarVisibility => IsStarred ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public int? RecipientCount { get; set; }
        public int? UnreadRecipientCount { get; set; }
        public List<WorkMemoAttachmentModel> Attachments { get; set; } = new();
        public bool HasAttachments => Attachments.Count > 0;
    }

    public class WorkMemoAttachmentModel
    {
        public long Id { get; set; }
        public long MemoId { get; set; }
        public string? OriginalFilename { get; set; }
        public string? ContentType { get; set; }
        public long FileSize { get; set; }
        public string? AttachmentType { get; set; }
        public bool IsImage => string.Equals(AttachmentType, "IMAGE", StringComparison.OrdinalIgnoreCase);
        public string DownloadUrl => $"{EmployeeModel.GetBaseUrl()}/api/work-memos/attachments/{Id}";
        public string ThumbnailUrl => DownloadUrl + (IsImage ? "?w=240" : "");
        public string SizeLabel => FileSize >= 1024 * 1024
            ? $"{FileSize / 1024d / 1024d:0.#}MB"
            : $"{Math.Max(1, FileSize / 1024)}KB";
    }

    public class WorkMemoRecipientModel
    {
        public long Id { get; set; }
        public long RecipientUserId { get; set; }
        public string? RecipientName { get; set; }
        public DateTime? ReadAt { get; set; }
        public bool IsStarred { get; set; }
        public bool IsUnread => ReadAt == null;
    }
}
