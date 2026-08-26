using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NexaOne.Web.Services.Auth;

namespace NexaOne.Web.Services.Api;

public sealed class ApiClient : IApiClient
{
    private readonly HttpClient _http;
    private readonly AuthTokenService _tokenService;
    private readonly JwtAuthStateProvider _authState;
    private readonly ApiNotificationService _notifier;
    private readonly UiTextService _ui;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private static readonly JwtSecurityTokenHandler _jwtHandler = new();

    public ApiClient(HttpClient http, AuthTokenService tokenService, JwtAuthStateProvider authState,
        ApiNotificationService notifier, UiTextService ui)
    {
        _http = http;
        _tokenService = tokenService;
        _authState = authState;
        _notifier = notifier;
        _ui = ui;
    }

    // ── Token helpers ─────────────────────────────────────────────────────────

    private static bool IsTokenExpiredOrExpiringSoon(string token)
    {
        try
        {
            if (!_jwtHandler.CanReadToken(token)) return true;
            var jwt = _jwtHandler.ReadJwtToken(token);
            return jwt.ValidTo <= DateTime.UtcNow.AddSeconds(60);
        }
        catch { return true; }
    }

    private async Task<string?> RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // double-check: another concurrent call may have already refreshed
            var current = await _tokenService.GetAccessTokenAsync();
            if (!string.IsNullOrEmpty(current) && !IsTokenExpiredOrExpiringSoon(current))
                return current;

            var userId = await _tokenService.GetUserIdAsync();
            var refreshToken = await _tokenService.GetRefreshTokenAsync();
            if (userId is null || refreshToken is null)
            {
                await _tokenService.ClearAsync();
                _authState.NotifyAuthChanged(null);
                return null;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/refresh");
            req.Content = JsonContent.Create(new { userId, refreshToken });
            // send expired token so the server can read user claims from it
            if (!string.IsNullOrEmpty(current))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current);

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                await _tokenService.ClearAsync();
                _authState.NotifyAuthChanged(null);
                return null;
            }

            var payload = await resp.Content.ReadFromJsonAsync<RefreshTokenPayload>(ct);
            if (payload is null)
            {
                await _tokenService.ClearAsync();
                _authState.NotifyAuthChanged(null);
                return null;
            }

            await _tokenService.SaveAsync(payload.AccessToken, payload.RefreshToken, userId);
            return payload.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // §20.9: SignalR AccessTokenProvider는 자동/수동 재연결 협상마다 호출된다 — 만료(임박) 토큰을
    // 그대로 공급하면 재연결이 401로 전부 실패하므로 HTTP 호출과 같은 갱신 경로를 태운다.
    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        var token = await _tokenService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;
        return IsTokenExpiredOrExpiringSoon(token) ? await RefreshAsync(ct) : token;
    }

    // 요청별 Authorization 헤더로 전송한다 — 공유 HttpClient.DefaultRequestHeaders를 변이하지 않아
    // 동시 요청 간 토큰 경합/오염이 없다(#5/#31). 401 시 1회 갱신 후 재전송하며 응답은 항상 Dispose.
    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string url,
        object? body,
        string? token,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // 서버 오류 메시지 다국어(P3-14) — 사용자 언어를 Accept-Language로 전파해 서버 응답 경계가
        // Error.Description을 번역하게 한다(모든 query/command/GET/POST의 중앙 경로).
        req.Headers.Add("Accept-Language", _ui.Language == "EnUs" ? "en-US" : "ko-KR");
        if (headers is not null)
            foreach (var (name, value) in headers)
                req.Headers.TryAddWithoutValidation(name, value);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return await _http.SendAsync(req, ct);
    }

    // surfaceErrors=true면 아무 페이지도 처리하지 않는 403/5xx를 전역 토스트로 노출한다.
    // 자체적으로 오류 사유를 표시하는 메서드(PostWithError/PatchWithError)는 false로 호출해 중복 노출을 막는다.
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body, CancellationToken ct,
        bool surfaceErrors = true,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        HttpResponseMessage resp;
        try
        {
            var token = await GetValidAccessTokenAsync(ct);
            resp = await SendOnceAsync(method, url, body, token, ct, headers);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                resp.Dispose();                   // 첫 401 응답 소켓/리소스 해제(누수 방지)
                var refreshed = await RefreshAsync(ct);
                resp = await SendOnceAsync(method, url, body, refreshed, ct, headers);
            }
        }
        // 전송 계층 실패(연결 거부·타임아웃)를 합성 503으로 변환한다 — 헬퍼들의 IsSuccessStatusCode 분기와
        // GetListAsync의 "예외 흡수" 계약을 실제로 지키고, Blazor Server 회로가 미처리 예외로 죽는 것을 막는다.
        // (사용자 취소(ct)는 그대로 전파 — 타임아웃으로 위장된 TaskCanceledException만 변환한다.)
        catch (HttpRequestException)
        {
            resp = ConnectionFailureResponse();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            resp = ConnectionFailureResponse();
        }
        if (surfaceErrors) await SurfaceUnhandledErrorAsync(resp, ct);
        return resp;
    }

    private static HttpResponseMessage ConnectionFailureResponse()
        => new(HttpStatusCode.ServiceUnavailable)
        {
            Content = JsonContent.Create(new
            {
                code = "SERVER_UNREACHABLE",
                description = "서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요."
            })
        };

    // 페이지가 인라인으로 처리하지 않는 실패를 전역 토스트로 통지한다: 403(권한 거부, ADR-003 module:manage)·5xx(서버
    // 오류)는 일반 메시지, 그 외 4xx(400/409/422 검증·충돌)는 서버 Error.Description을 노출한다. 자체 사유 표시
    // 메서드(PostWithError/PatchWithError)는 surfaceErrors:false로 호출돼 여기 오지 않으므로 중복 통지가 없다.
    // (이전엔 400/409를 제외해, plain Post로 저장하는 마스터데이터 등록 다이얼로그의 실패가 조용히 삼켜졌다 — 교정.)
    private async Task SurfaceUnhandledErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Unauthorized)
            return;   // 401은 인증 흐름(RefreshAsync의 갱신/로그아웃)이 처리한다
        // 클라이언트 생성 일반 오류 문구(권한/서버/연결/폴백)는 현재 언어로 번역한다(P3-14 v4).
        // 서버 모듈 Error.Description(자유 문장)은 ReadErrorAsync가 그대로 노출 — 서버측 메시지 다국어는 별도 아크.
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            _notifier.Notify(_ui.T("error.forbidden", "이 작업을 수행할 권한이 없습니다. 관리자에게 권한을 요청하세요."));
        else if ((int)resp.StatusCode >= 500)
            _notifier.Notify(string.Format(
                _ui.T("error.server", "서버 오류가 발생했습니다 (HTTP {0}). 잠시 후 다시 시도해 주세요."), (int)resp.StatusCode));
        else
            _notifier.Notify(await ReadErrorAsync(resp, ct));
    }

    private async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var error = await resp.Content.ReadFromJsonAsync<ApiErrorPayload>(ct);
            // 클라이언트 합성 오류(연결 실패)는 코드로 식별해 현재 언어로 번역한다.
            if (error?.Code == "SERVER_UNREACHABLE")
                return _ui.T("error.unreachable", error.Description ?? "서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요.");
            // 서버 모듈 Error.Description은 그대로 노출(서버측 다국어는 별도 아크).
            if (!string.IsNullOrEmpty(error?.Description))
                return error.Description;
        }
        catch { /* 오류 본문이 표준 형식이 아니면 상태 코드로 폴백 */ }
        return string.Format(_ui.T("error.requestFailed", "요청에 실패했습니다 (HTTP {0})."), (int)resp.StatusCode);
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var resp = await SendAsync(HttpMethod.Get, url, null, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<T>(ct) : null;
    }

    // 실패(예외/오류 응답)를 빈 리스트로 흡수한다 — ContinueWith(t => t.Result) 안티패턴(#6/#24) 대체.
    private async Task<List<T>> GetListAsync<T>(string url, CancellationToken ct)
        => await GetAsync<List<T>>(url, ct) ?? new List<T>();

    private async Task<T?> PostAsync<T>(string url, object body, CancellationToken ct) where T : class
    {
        using var resp = await SendAsync(HttpMethod.Post, url, body, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<T>(ct) : null;
    }

    private async Task PutAsync(string url, object? body, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Put, url, body ?? new { }, ct);
    }

    // 상태전이 POST용(응답 본문 불필요) — 통합 호스트 브리지 전이 엔드포인트는 POST 규약이다(구 API의 PUT 아님).
    private async Task PostAsync(string url, object? body, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, url, body ?? new { }, ct);
    }

    // 본문 있는 DELETE용 — 호스트 권한 회수(DELETE roles/{id}/permissions)는 권한 문자열을 본문으로 받는다.
    private async Task<bool> DeleteWithBodyAsync(string url, object body, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Delete, url, body, ct);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> DeleteAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Delete, url, null, ct);
        return resp.IsSuccessStatusCode;
    }

    // §19.4 — 검증 실패 사유(Error.Description)를 화면에 보여줘야 하는 POST용
    private async Task<(T? Result, string? Error)> PostWithErrorAsync<T>(
        string url, object body, CancellationToken ct) where T : class
    {
        using var resp = await SendAsync(HttpMethod.Post, url, body, ct, surfaceErrors: false);
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<T>(ct), null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return (null, "인증이 만료되었습니다. 다시 로그인해 주세요.");
        return (null, await ReadErrorAsync(resp, ct));
    }

    // §20.12 — 본문 없는 204 응답의 성공 여부가 필요한 POST용 (PostAsync<T>는 본문 역직렬화 전제)
    private async Task<bool> PostForStatusAsync(string url, object? body, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Post, url, body ?? new { }, ct);
        return resp.IsSuccessStatusCode;
    }

    // §20.11/§20.12 — 성공 여부가 필요한 PUT용 (PutAsync는 실패를 보고하지 않는다)
    private async Task<bool> PutForStatusAsync(string url, object? body, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Put, url, body ?? new { }, ct);
        return resp.IsSuccessStatusCode;
    }

    // §19.3 — 승인/반려 PATCH용. 실패 사유(Error.Description)를 화면에 표시한다
    private async Task<(T? Result, string? Error)> PatchWithErrorAsync<T>(
        string url, object body, CancellationToken ct) where T : class
    {
        using var resp = await SendAsync(HttpMethod.Patch, url, body, ct, surfaceErrors: false);
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<T>(ct), null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return (null, "인증이 만료되었습니다. 다시 로그인해 주세요.");
        return (null, await ReadErrorAsync(resp, ct));
    }

    private sealed record RefreshTokenPayload(string AccessToken, string RefreshToken);
    private sealed record LoginErrorPayload(string? Code, string? Message);
    /// <summary>API 표준 오류 본문(NexaOne.Common.Error 직렬화 형태) — 업로드 실패 사유 표시용.</summary>
    private sealed record ApiErrorPayload(string? Code, string? Description);

    // ── 파일 기반 쿼리 레지스트리(저코드 경로) ──────────────────────────────────
    // 등록된 query id를 파라미터와 함께 실행해 동적 행 목록을 받는다. 실패는 빈 목록으로 흡수(그리드 안전).
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        string queryId, object? parameters = null, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            HttpMethod.Post, $"api/v1/query/{Uri.EscapeDataString(queryId)}", parameters ?? new { }, ct);
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<Dictionary<string, object?>>();
        return await resp.Content.ReadFromJsonAsync<List<Dictionary<string, object?>>>(ct)
            ?? new List<Dictionary<string, object?>>();
    }

    // MRP 실행 — 브리지 REST(pom:manage). 실패는 null(403/5xx 사유는 SendAsync 전역 토스트).
    public async Task<MrpRunResultDto?> RunMrpAsync(int? bucketDays = null, int? horizonBuckets = null, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Post, "api/v1/pom/mrp/run", new { bucketDays, horizonBuckets }, ct);
        if (!resp.IsSuccessStatusCode) return null;
        try { return await resp.Content.ReadFromJsonAsync<MrpRunResultDto>(ct); }
        catch { return null; }
    }

    // MRP 실오더 전환 — 생산 제안별 설비 배정을 포함해 단일 트랜잭션으로 전환한다.
    public async Task<MrpConvertResultDto?> ConvertMrpAsync(
        string? runId = null,
        IReadOnlyList<string>? plannedOrderIds = null,
        IReadOnlyList<MrpProductionAssignmentDto>? productionAssignments = null,
        CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Post, "api/v1/pom/mrp/convert", new { runId, plannedOrderIds, productionAssignments }, ct);
        if (!resp.IsSuccessStatusCode) return null;
        try { return await resp.Content.ReadFromJsonAsync<MrpConvertResultDto>(ct); }
        catch { return null; }
    }

    // 제네릭 서버 페이징 — 등록 read 쿼리를 페이징 절로 감싼 {total, rows}. 실패(404/422/구버전)는 null로
    // 신호해 호출측이 전량 경로(ExecuteQueryAsync)로 폴백한다(하이브리드 페이징의 안전판).
    public async Task<PagedQueryResult?> ExecuteQueryPagedAsync(
        string queryId, object? parameters = null, int limit = 500, int offset = 0, CancellationToken ct = default)
    {
        // 404/422는 오류가 아니라 호출측이 전량 조회로 전환하기 위한 기능 협상 신호다.
        // 전역 토스트를 띄우면 자체 LIMIT 쿼리를 쓰는 대시보드가 정상 폴백하면서도 매 새로고침마다
        // 실패처럼 보이므로, 이 경로는 응답을 조용히 해석하고 실제 전량 조회의 실패만 표면화한다.
        using var resp = await SendAsync(
            HttpMethod.Post, $"api/v1/query/{Uri.EscapeDataString(queryId)}/paged",
            new { parameters = parameters ?? new { }, limit, offset }, ct,
            surfaceErrors: false);
        if (!resp.IsSuccessStatusCode) return null;
        try { return await resp.Content.ReadFromJsonAsync<PagedQueryResult>(ct); }
        catch { return null; }
    }

    // 등록된 쓰기(command) query id를 실행한다(메타 화면 폼 저장 등). 성공 여부를 반환하고,
    // 403/5xx는 SendAsync가 전역 토스트로 노출한다(감사 컬럼은 게이트웨이가 토큰·UTC로 주입).
    public async Task<bool> ExecuteCommandAsync(
        string queryId, object? parameters = null, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            HttpMethod.Post, $"api/v1/command/{Uri.EscapeDataString(queryId)}", parameters ?? new { }, ct);
        return await IsCommandAppliedAsync(resp, ct);
    }

    /// <summary>
    /// 명명 command의 HTTP 결과와 영향 행 수를 실제 업무 성공 여부로 변환합니다.
    /// 별도 command 구현의 구형 빈 응답은 호환을 위해 성공으로 보되, 표준 <c>{ affected: 0 }</c> 응답은
    /// 상태 가드·입력 검증에 막힌 것으로 판단해 실패로 반환합니다.
    /// </summary>
    internal static async Task<bool> IsCommandAppliedAsync(
        HttpResponseMessage response, CancellationToken ct = default)
    {
        if (!response.IsSuccessStatusCode) return false;

        // 명명 쓰기쿼리는 HTTP 200과 함께 영향 행 수를 반환한다. 상태 가드나 입력 검증으로 0행이면
        // 전송 자체는 성공했어도 업무 저장은 실패이므로 false로 알려 폼이 성공 표시/닫기를 하지 않게 한다.
        try
        {
            var result = await response.Content.ReadFromJsonAsync<AffectedRowsPayload>(ct);
            return result is null || result.Affected > 0;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // 구버전 서버나 별도 command 구현의 빈 성공 응답은 기존 호환 동작을 유지한다.
            return true;
        }
    }

    private sealed record AffectedRowsPayload(int Affected);

    // ── Auth ──────────────────────────────────────────────────────────────────

    public async Task<LoginResult> LoginAsync(string userId, string password, CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync("api/v1/auth/login",
                new { userId, password, plantId = "DEFAULT" }, ct);
        }
        // 로그인은 SendAsync를 거치지 않으므로(토큰 불요) 전송 실패를 여기서 흡수한다 — 회로 사망 방지
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new LoginResult(null, "SERVER_UNREACHABLE", "서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요.");
        }
        if (resp.IsSuccessStatusCode)
            return new LoginResult(await resp.Content.ReadFromJsonAsync<LoginResponse>(ct));

        // §20.10 — 401 응답의 code/message를 보존해 계정 잠금 안내를 표시한다
        try
        {
            var error = await resp.Content.ReadFromJsonAsync<LoginErrorPayload>(ct);
            return new LoginResult(null, error?.Code, error?.Message);
        }
        catch
        {
            return new LoginResult(null);
        }
    }

    public async Task LogoutAsync(string userId, string refreshToken, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, "api/v1/auth/logout", new { userId, refreshToken }, ct);
    }

    public async Task ForgotPasswordAsync(string userId, string email, CancellationToken ct = default)
    {
        // 존재 여부 비노출 정책상 페이지는 결과와 무관하게 동일 안내를 표시한다 — 전송 실패도 조용히 흡수
        try { await _http.PostAsJsonAsync("api/v1/auth/forgot-password", new { userId, email }, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
    }

    // 실패 사유(정책 미달·현재 비밀번호 불일치 등)를 화면에 보여줘야 하므로 (Ok, Error)를 반환한다 —
    // 예외 전파(EnsureSuccessStatusCode)는 Blazor Server 이벤트 핸들러에서 회로 사망 위험이 있어 쓰지 않는다.
    public async Task<(bool Ok, string? Error)> ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Post, "api/v1/auth/change-password",
            new { currentPassword, newPassword, confirmPassword }, ct, surfaceErrors: false);
        if (!resp.IsSuccessStatusCode)
            return (false, await ReadErrorAsync(resp, ct));

        // §20.10 — 변경 성공 시 서버가 pwdChange 클레임 없는 새 토큰을 재발급한다.
        // 이전 토큰은 만료까지 업무 API가 차단되므로 즉시 교체한다.
        var payload = await resp.Content.ReadFromJsonAsync<RefreshTokenPayload>(ct);
        var userId = await _tokenService.GetUserIdAsync();
        if (payload is not null && userId is not null)
        {
            await _tokenService.SaveAsync(payload.AccessToken, payload.RefreshToken, userId);
            _authState.NotifyAuthChanged(null);   // 새 토큰 기준으로 인증 상태 재평가
        }
        return (true, null);
    }

    // ── MDM ───────────────────────────────────────────────────────────────────

    public Task<List<EquipmentDto>> GetEquipmentListAsync(string plantId, CancellationToken ct = default)
        => GetListAsync<EquipmentDto>($"api/v1/mdm/equipment?plantId={plantId}", ct);

    public Task<EquipmentDto?> GetEquipmentAsync(string id, CancellationToken ct = default)
        => GetAsync<EquipmentDto>($"api/v1/mdm/equipment/{id}", ct);

    public Task<EquipmentDto?> CreateEquipmentAsync(object req, CancellationToken ct = default)
        => PostAsync<EquipmentDto>("api/v1/mdm/equipment", req, ct);

    public Task DeleteEquipmentAsync(string id, CancellationToken ct = default)
        => DeleteAsync($"api/v1/mdm/equipment/{id}", ct);

    public Task<List<PlantDto>> GetPlantsAsync(CancellationToken ct = default)
        => GetListAsync<PlantDto>("api/v1/mdm/plants", ct);

    public Task<PlantDto?> CreatePlantAsync(object req, CancellationToken ct = default)
        => PostAsync<PlantDto>("api/v1/mdm/plants", req, ct);

    public Task<List<AreaDto>> GetAreasAsync(string plantId, CancellationToken ct = default)
        => GetListAsync<AreaDto>($"api/v1/mdm/areas?plantId={plantId}", ct);

    public Task<AreaDto?> CreateAreaAsync(object req, CancellationToken ct = default)
        => PostAsync<AreaDto>("api/v1/mdm/areas", req, ct);

    public Task<List<ProductDto>> GetProductsAsync(CancellationToken ct = default)
        => GetListAsync<ProductDto>("api/v1/mdm/products", ct);

    public Task<ProductDto?> CreateProductAsync(object req, CancellationToken ct = default)
        => PostAsync<ProductDto>("api/v1/mdm/products", req, ct);

    public Task<List<CodeClassDto>> GetCodeClassesAsync(CancellationToken ct = default)
        => GetListAsync<CodeClassDto>("api/v1/mdm/code-classes", ct);

    public Task<CodeClassDto?> CreateCodeClassAsync(object req, CancellationToken ct = default)
        => PostAsync<CodeClassDto>("api/v1/mdm/code-classes", req, ct);

    public Task<List<CodeDto>> GetCodesAsync(string codeClassId, CancellationToken ct = default)
        => GetListAsync<CodeDto>($"api/v1/mdm/codes?codeClassId={codeClassId}", ct);

    public Task<CodeDto?> CreateCodeAsync(object req, CancellationToken ct = default)
        => PostAsync<CodeDto>("api/v1/mdm/codes", req, ct);

    // ── EPT ───────────────────────────────────────────────────────────────────

    public Task<List<EquipmentStateMatrixDto>> GetStateMatrixAsync(string plantId, CancellationToken ct = default)
        => GetListAsync<EquipmentStateMatrixDto>($"api/v1/est/state-matrix?plantId={plantId}", ct);

    public Task<List<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(string plantId, string fromState, CancellationToken ct = default)
        => GetListAsync<EquipmentStateMatrixDto>($"api/v1/est/state-matrix/allowed?plantId={plantId}&fromState={fromState}", ct);

    public Task<EquipmentStateMatrixDto?> UpsertStateMatrixAsync(object req, CancellationToken ct = default)
        => PostAsync<EquipmentStateMatrixDto>("api/v1/est/state-matrix", req, ct);

    // 실패(null)와 빈 결과를 구분한다 — 폴백/병합 갱신 실패가 표시 중인 그리드를 비우지 않도록 (§20.9)
    public Task<List<EquipmentCurrentStateDto>?> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default)
        => GetAsync<List<EquipmentCurrentStateDto>>($"api/v1/est/equipment-state?plantId={plantId}", ct);

    public Task<EquipmentCurrentStateDto?> ChangeEquipmentStateAsync(object req, CancellationToken ct = default)
        => PostAsync<EquipmentCurrentStateDto>("api/v1/est/equipment-state/change", req, ct);

    public Task<List<EquipmentStateHistoryDto>> GetStateHistoryAsync(string equipmentId, CancellationToken ct = default)
        => GetListAsync<EquipmentStateHistoryDto>($"api/v1/est/equipment-state/{equipmentId}/history", ct);

    // 실패(null)와 빈 결과를 구분한다 — 폴백/병합 갱신 실패가 표시 중인 그리드를 비우지 않도록 (§20.9)
    public Task<List<AlarmDto>?> GetAlarmsAsync(string plantId, CancellationToken ct = default)
        => GetAsync<List<AlarmDto>>($"api/v1/est/alarms?plantId={plantId}", ct);

    public Task ClearAlarmAsync(string alarmId, CancellationToken ct = default)
        => PostAsync($"api/v1/est/alarms/{alarmId}/clear", new { ClearedAt = DateTime.UtcNow }, ct);

    // ── FDC ───────────────────────────────────────────────────────────────────

    public Task<List<InterlockRuleDto>> GetInterlockRulesAsync(string equipmentId, CancellationToken ct = default)
        => GetListAsync<InterlockRuleDto>($"api/v1/fdc/interlock-rules?equipmentId={equipmentId}", ct);

    public Task<InterlockRuleDto?> CreateInterlockRuleAsync(object req, CancellationToken ct = default)
        => PostAsync<InterlockRuleDto>("api/v1/fdc/interlock-rules", req, ct);

    public Task<List<FdcParameterDto>> GetFdcParametersAsync(string equipmentId, CancellationToken ct = default)
        => GetListAsync<FdcParameterDto>($"api/v1/fdc/parameters?equipmentId={equipmentId}", ct);

    public Task<FdcParameterDto?> CreateFdcParameterAsync(object req, CancellationToken ct = default)
        => PostAsync<FdcParameterDto>("api/v1/fdc/parameters", req, ct);

    public Task<List<FdcCollectDataDto>> GetFdcCollectDataAsync(string parameterId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var url = $"api/v1/fdc/collect-data?parameterId={parameterId}&from={from:O}&to={to:O}";
        return GetListAsync<FdcCollectDataDto>(url, ct);
    }

    // 실패(null)와 빈 결과를 구분해야 한다 — 실패한 조회를 '최근 조건'으로 자동 저장하지 않기 위함 (설계 20.8)
    public Task<List<FdcCollectDataDto>?> GetLatestFdcDataAsync(string parameterId, int limit = 50, CancellationToken ct = default)
        => GetAsync<List<FdcCollectDataDto>>($"api/v1/fdc/collect-data/latest?parameterId={parameterId}&limit={limit}", ct);

    // 컨트롤러는 { CollectedData, Interlock } 래퍼를 반환하므로 래퍼로 역직렬화 후 CollectedData를 꺼낸다.
    // (평면 FdcCollectDataDto로 받으면 모든 필드가 비어 PostAsync의 null=실패 규약이 무력화됨)
    public async Task<FdcCollectDataDto?> RecordFdcDataAsync(object req, CancellationToken ct = default)
    {
        var result = await PostAsync<FdcRecordResultDto>("api/v1/fdc/collect-data", req, ct);
        return result?.CollectedData;
    }

    public Task<List<FdcInterlockHistoryDto>> GetInterlockHistoryAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default)
        => GetListAsync<FdcInterlockHistoryDto>($"api/v1/fdc/interlock-history?equipmentId={equipmentId}&from={from:o}&to={to:o}", ct);

    // Low-Code 화면 정의 저장소(SYS_SCREEN_DEFINITION) — 통합 호스트에는 전용 REST가 없고 명명 쿼리/커맨드
    // 게이트웨이가 단일 경로다(SPA 디자이너와 동일 원천). 구 api/v1/sys/screen-definitions REST는 API 폐기와 함께 소멸.
    public Task<List<ScreenDefinitionRecordDto>> GetScreenDefinitionsAsync(CancellationToken ct = default)
        => GetScreenDefinitionsAsync(null, ct);

    public async Task<List<ScreenDefinitionRecordDto>> GetScreenDefinitionsAsync(
        string? targetChannel, CancellationToken ct = default)
        => (await ExecuteQueryAsync("SYS.ListScreenDefinitions", new { targetChannel }, ct))
            .Select(r => ScreenRecord(r, includeDefinition: false))
            .ToList();

    public async Task<ScreenDefinitionRecordDto?> GetScreenDefinitionAsync(string uiId, CancellationToken ct = default)
    {
        var rows = await ExecuteQueryAsync("SYS.GetScreenDefinition", new { uiId }, ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : ScreenRecord(r, includeDefinition: true);
    }

    public Task SaveScreenDefinitionAsync(string uiId, string title, string definitionJson, CancellationToken ct = default)
        => SaveScreenDefinitionAsync(uiId, title, definitionJson, "MES", null, ct);

    public Task SaveScreenDefinitionAsync(
        string uiId, string title, string definitionJson,
        string targetChannel, string? entryPath = null, CancellationToken ct = default)
        => ExecuteCommandAsync(
            "SYS.UpsertScreenDefinition",
            new { uiId, title, definitionJson, targetChannel, entryPath }, ct);

    private static ScreenDefinitionRecordDto ScreenRecord(
        Dictionary<string, object?> row, bool includeDefinition)
    {
        var uiId = Col(row, "UI_ID");
        var channel = Col(row, "TARGET_CHANNEL");
        if (string.IsNullOrWhiteSpace(channel)) channel = "MES";
        var entryPath = Col(row, "ENTRY_PATH");
        if (string.IsNullOrWhiteSpace(entryPath)) entryPath = $"/meta/{uiId}";
        return new ScreenDefinitionRecordDto(
            uiId,
            Col(row, "TITLE"),
            includeDefinition ? Col(row, "DEFINITION_JSON") : string.Empty,
            channel,
            entryPath);
    }

    private static string Col(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;

    // 명명 쿼리 카탈로그(sys:manage) — S/O 관리(메타 카탈로그) 화면용.
    public Task<List<QueryCatalogItemDto>> GetQueryCatalogAsync(CancellationToken ct = default)
        => GetListAsync<QueryCatalogItemDto>("api/v1/sys/queries", ct);

    public Task<List<FdcParameterGroupDto>> GetFdcParameterGroupsAsync(string equipmentId, CancellationToken ct = default)
        => GetListAsync<FdcParameterGroupDto>($"api/v1/fdc/parameter-groups?equipmentId={equipmentId}", ct);

    public Task<FdcParameterGroupDto?> CreateFdcParameterGroupAsync(object req, CancellationToken ct = default)
        => PostAsync<FdcParameterGroupDto>("api/v1/fdc/parameter-groups", req, ct);

    public Task<List<FdcAlarmConfigDto>> GetFdcAlarmConfigsAsync(string equipmentId, CancellationToken ct = default)
        => GetListAsync<FdcAlarmConfigDto>($"api/v1/fdc/alarm-configs?equipmentId={equipmentId}", ct);

    public Task<FdcAlarmConfigDto?> CreateFdcAlarmConfigAsync(object req, CancellationToken ct = default)
        => PostAsync<FdcAlarmConfigDto>("api/v1/fdc/alarm-configs", req, ct);

    public Task<List<FdcAlarmHistoryDto>> GetFdcAlarmHistoryAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default)
        => GetListAsync<FdcAlarmHistoryDto>($"api/v1/fdc/alarm-history?equipmentId={equipmentId}&from={from:o}&to={to:o}", ct);

    // ── RMS ───────────────────────────────────────────────────────────────────

    public Task<List<RecipeDto>> GetRecipesAsync(string? equipmentClassId = null, string? state = null, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(equipmentClassId)) qs.Add($"equipmentClassId={equipmentClassId}");
        if (!string.IsNullOrEmpty(state)) qs.Add($"state={state}");
        var url = "api/v1/rms/recipes" + (qs.Any() ? "?" + string.Join("&", qs) : "");
        return GetListAsync<RecipeDto>(url, ct);
    }

    public Task<RecipeDto?> CreateRecipeAsync(object req, CancellationToken ct = default)
        => PostAsync<RecipeDto>("api/v1/rms/recipes", req, ct);

    public Task RequestRecipeApprovalAsync(string recipeId, CancellationToken ct = default)
        => PutAsync($"api/v1/rms/recipes/{recipeId}/request-approval", null, ct);

    public Task ApproveRecipe1Async(string recipeId, string approverId, CancellationToken ct = default)
        => PutAsync($"api/v1/rms/recipes/{recipeId}/approve1", new { approverId }, ct);

    public Task ApproveRecipe2Async(string recipeId, string approverId, CancellationToken ct = default)
        => PutAsync($"api/v1/rms/recipes/{recipeId}/approve2", new { approverId }, ct);

    public Task ReleaseRecipeAsync(string recipeId, string approverId, CancellationToken ct = default)
        => PutAsync($"api/v1/rms/recipes/{recipeId}/release", new { approverId }, ct);

    public Task RejectRecipeAsync(string recipeId, string reason, CancellationToken ct = default)
        => PutAsync($"api/v1/rms/recipes/{recipeId}/reject", new { reason }, ct);

    public Task<RecipeDto?> CreateRecipeVersionAsync(string recipeId, string newRecipeId, CancellationToken ct = default)
        => PostAsync<RecipeDto>($"api/v1/rms/recipes/{recipeId}/new-version", new { newRecipeId }, ct);

    public Task<List<RecipeParamDto>> GetRecipeParamsAsync(string recipeId, CancellationToken ct = default)
        => GetListAsync<RecipeParamDto>($"api/v1/rms/recipes/{recipeId}/params", ct);

    public Task<RecipeParamDto?> AddRecipeParamAsync(string recipeId, object req, CancellationToken ct = default)
        => PostAsync<RecipeParamDto>($"api/v1/rms/recipes/{recipeId}/params", req, ct);

    public Task UpdateRecipeParamAsync(string paramId, string newValue, CancellationToken ct = default)
        => PutAsync($"api/v1/rms/recipes/params/{paramId}", new { newValue }, ct);

    public Task DeleteRecipeParamAsync(string paramId, CancellationToken ct = default)
        => DeleteAsync($"api/v1/rms/recipes/params/{paramId}", ct);

    // ── QMS ───────────────────────────────────────────────────────────────────

    public Task<List<DefectDto>> GetDefectsAsync(string lotId, CancellationToken ct = default)
        => GetListAsync<DefectDto>($"api/v1/qms/defects?lotId={lotId}", ct);

    public Task<DefectDto?> RecordDefectAsync(object req, CancellationToken ct = default)
        => PostAsync<DefectDto>("api/v1/qms/defects", req, ct);

    public Task ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default)
        => PostAsync($"api/v1/qms/defects/{defectId}/confirm", new { confirmerId }, ct);

    public Task<List<DefectClassDto>> GetDefectClassesAsync(CancellationToken ct = default)
        => GetListAsync<DefectClassDto>("api/v1/qms/defect-classes", ct);

    public Task<DefectClassDto?> CreateDefectClassAsync(object req, CancellationToken ct = default)
        => PostAsync<DefectClassDto>("api/v1/qms/defect-classes", req, ct);

    public Task<List<InspectionSpecDto>> GetInspectionSpecsAsync(string? processId = null, CancellationToken ct = default)
    {
        var url = "api/v1/qms/inspection-specs" +
            (string.IsNullOrEmpty(processId) ? "" : $"?processId={processId}");
        return GetListAsync<InspectionSpecDto>(url, ct);
    }

    public Task<InspectionSpecDto?> CreateInspectionSpecAsync(object req, CancellationToken ct = default)
        => PostAsync<InspectionSpecDto>("api/v1/qms/inspection-specs", req, ct);

    public Task<List<InspectionResultDto>> GetInspectionResultsAsync(string lotId, CancellationToken ct = default)
        => GetListAsync<InspectionResultDto>($"api/v1/qms/inspection-results?lotId={lotId}", ct);

    public Task<InspectionResultDto?> RecordInspectionResultAsync(object req, CancellationToken ct = default)
        => PostAsync<InspectionResultDto>("api/v1/qms/inspection-results", req, ct);

    public async Task<InspectionExecutionApiResult> RecordInspectionExecutionV2Async(
        RecordInspectionExecutionV2Request req, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v2/qms/inspection-executions",
            req,
            ct,
            surfaceErrors: false,
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Idempotency-Key"] = req.IdempotencyKey
            });
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var dto = await response.Content.ReadFromJsonAsync<InspectionExecutionV2Dto>(ct);
                return dto is not null
                    ? new(dto, null, (int)response.StatusCode)
                    : new(null, "검사 실행 응답을 읽을 수 없습니다.", (int)response.StatusCode);
            }
            catch
            {
                return new(null, "검사 실행 응답을 읽을 수 없습니다.", (int)response.StatusCode);
            }
        }

        var error = response.StatusCode == HttpStatusCode.Unauthorized
            ? "인증이 만료되었습니다. 다시 로그인해 주세요."
            : await ReadErrorAsync(response, ct);
        return new(null, error, (int)response.StatusCode);
    }

    public Task<LotInspectionStatusDto?> GetLotInspectionStatusAsync(string lotId, CancellationToken ct = default)
        => GetAsync<LotInspectionStatusDto>($"api/v1/qms/lots/{Uri.EscapeDataString(lotId)}/inspection-status", ct);

    public Task<List<SpcParamDto>> GetSpcParamsAsync(string equipmentId, CancellationToken ct = default)
        => GetListAsync<SpcParamDto>($"api/v1/qms/spc-params?equipmentId={equipmentId}", ct);

    public Task<SpcParamDto?> CreateSpcParamAsync(object req, CancellationToken ct = default)
        => PostAsync<SpcParamDto>("api/v1/qms/spc-params", req, ct);

    public Task UpdateSpcLimitsAsync(string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default)
        => PostAsync($"api/v1/qms/spc-params/{paramId}/control-limits", new { mean, ucl, lcl }, ct);

    public Task<SpcLimitRevisionDto?> AddSpcLimitRevisionAsync(object req, CancellationToken ct = default)
        => PostAsync<SpcLimitRevisionDto>("api/v1/qms/spc/limit-revisions", req, ct);

    public Task<SpcSubgroupEvaluationDto?> EvaluateSpcSubgroupAsync(object req, CancellationToken ct = default)
        => PostAsync<SpcSubgroupEvaluationDto>("api/v1/qms/spc/subgroups/evaluate", req, ct);

    public Task<List<SpcRuleViolationDto>> GetSpcViolationsAsync(
        string? paramId = null, string? subgroupId = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(paramId)) query.Add($"paramId={Uri.EscapeDataString(paramId)}");
        if (!string.IsNullOrWhiteSpace(subgroupId)) query.Add($"subgroupId={Uri.EscapeDataString(subgroupId)}");
        return GetListAsync<SpcRuleViolationDto>("api/v1/qms/spc/violations" +
            (query.Count == 0 ? "" : "?" + string.Join("&", query)), ct);
    }

    public Task<SamplingPlanRevisionDto?> AddSamplingPlanRevisionAsync(object req, CancellationToken ct = default)
        => PostAsync<SamplingPlanRevisionDto>("api/v1/qms/sampling-plans/revisions", req, ct);

    public Task<SamplingPlanRevisionDto?> SelectSamplingPlanAsync(
        int lotSize, DateTime? effectiveAt = null, CancellationToken ct = default)
    {
        var url = $"api/v1/qms/sampling-plans/select?lotSize={lotSize}";
        if (effectiveAt.HasValue) url += $"&effectiveAt={Uri.EscapeDataString(effectiveAt.Value.ToString("O"))}";
        return GetAsync<SamplingPlanRevisionDto>(url, ct);
    }

    public Task<SamplingEvaluationDto?> EvaluateSamplingAsync(object req, CancellationToken ct = default)
        => PostAsync<SamplingEvaluationDto>("api/v1/qms/sampling-plans/evaluate", req, ct);

    public Task<AiModelVersionDto?> RegisterAiModelVersionAsync(object req, CancellationToken ct = default)
        => PostAsync<AiModelVersionDto>("api/v1/qms/ai/models/versions", req, ct);

    public Task<AiInferenceDto?> RecordAiInferenceAsync(object req, CancellationToken ct = default)
        => PostAsync<AiInferenceDto>("api/v1/qms/ai/inferences", req, ct);

    public Task<AiInferenceDto?> GetAiInferenceAsync(string inferenceId, CancellationToken ct = default)
        => GetAsync<AiInferenceDto>($"api/v1/qms/ai/inferences/{Uri.EscapeDataString(inferenceId)}", ct);

    public Task<List<AiReviewDto>> GetAiReviewsAsync(string inferenceId, CancellationToken ct = default)
        => GetListAsync<AiReviewDto>($"api/v1/qms/ai/inferences/{Uri.EscapeDataString(inferenceId)}/reviews", ct);

    public Task<AiReviewDto?> ReviewAiInferenceAsync(string inferenceId, object req, CancellationToken ct = default)
        => PostAsync<AiReviewDto>($"api/v1/qms/ai/inferences/{Uri.EscapeDataString(inferenceId)}/reviews", req, ct);

    // ── EMS ───────────────────────────────────────────────────────────────────

    public Task<List<WorkOrderDto>> GetWorkOrdersAsync(string? equipmentId = null, string? status = null, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(equipmentId)) qs.Add($"equipmentId={equipmentId}");
        if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
        var url = "api/v1/ems/work-orders" + (qs.Any() ? "?" + string.Join("&", qs) : "");
        return GetListAsync<WorkOrderDto>(url, ct);
    }

    public Task<WorkOrderDto?> CreateWorkOrderAsync(object req, CancellationToken ct = default)
        => PostAsync<WorkOrderDto>("api/v1/ems/work-orders", req, ct);

    public Task StartWorkOrderAsync(string woId, CancellationToken ct = default)
        => PostAsync($"api/v1/ems/work-orders/{woId}/start", null, ct);

    public Task CompleteWorkOrderAsync(string woId, string remark, CancellationToken ct = default)
        => PostAsync($"api/v1/ems/work-orders/{woId}/complete", new { remark }, ct);

    public Task CancelWorkOrderAsync(string woId, CancellationToken ct = default)
        => PostAsync($"api/v1/ems/work-orders/{woId}/cancel", null, ct);

    /// <summary>
    /// POM 작업지시 생성 API를 호출하고 도메인 검증 오류를 관리 화면에 그대로 반환합니다.
    /// </summary>
    public async Task<PomWorkOrderActionResult> CreatePomWorkOrderAsync(
        PomWorkOrderCreateRequest request,
        CancellationToken ct = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/pom/work-orders",
            request,
            ct,
            surfaceErrors: false);

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var dto = await response.Content.ReadFromJsonAsync<PomWorkOrderDto>(ct);
                return dto is not null
                    ? new(dto, null, (int)response.StatusCode)
                    : new(null, "작업지시 생성 응답을 읽을 수 없습니다.", (int)response.StatusCode);
            }
            catch
            {
                return new(null, "작업지시 생성 응답을 읽을 수 없습니다.", (int)response.StatusCode);
            }
        }

        var error = response.StatusCode == HttpStatusCode.Unauthorized
            ? "인증이 만료되었습니다. 다시 로그인해 주세요."
            : await ReadErrorAsync(response, ct);
        return new(null, error, (int)response.StatusCode);
    }

    /// <summary>
    /// POM 작업지시의 상태전이를 typed REST API로 실행합니다. URL과 본문 형태는 허용된 액션으로 고정해
    /// Designer 값이 임의의 엔드포인트를 호출하지 못하게 하고, 실패 사유와 409 상태 코드를 함께 보존합니다.
    /// </summary>
    public async Task<PomWorkOrderActionResult> ExecutePomWorkOrderActionAsync(
        string action,
        string workOrderId,
        PomWorkOrderActionRequest request,
        CancellationToken ct = default)
    {
        var normalizedAction = action?.Trim().ToLowerInvariant();
        if (normalizedAction is not ("release" or "cancel" or "start" or "report" or "hold" or "release-hold" or "complete"))
            return new(null, $"지원하지 않는 작업지시 액션입니다: {action}", 400);

        if (string.IsNullOrWhiteSpace(workOrderId))
            return new(null, "작업지시 ID가 필요합니다.", 400);

        object body = normalizedAction is "report" or "complete"
            ? new
            {
                goodQty = request.GoodQty,
                defectQty = request.DefectQty,
                request.IdempotencyKey,
                request.ExpectedVersion,
                request.ClientChannel,
                request.DeviceId,
                request.Remark
            }
            : new
            {
                request.IdempotencyKey,
                request.ExpectedVersion,
                request.ClientChannel,
                request.DeviceId,
                request.Remark
            };

        var id = Uri.EscapeDataString(workOrderId.Trim());
        using var response = await SendAsync(
            HttpMethod.Post,
            $"api/v1/pom/work-orders/{id}/{normalizedAction}",
            body,
            ct,
            surfaceErrors: false);

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var dto = await response.Content.ReadFromJsonAsync<PomWorkOrderDto>(ct);
                return dto is not null
                    ? new(dto, null, (int)response.StatusCode)
                    : new(null, "작업지시 응답을 읽을 수 없습니다.", (int)response.StatusCode);
            }
            catch
            {
                return new(null, "작업지시 응답을 읽을 수 없습니다.", (int)response.StatusCode);
            }
        }

        var error = response.StatusCode == HttpStatusCode.Unauthorized
            ? "인증이 만료되었습니다. 다시 로그인해 주세요."
            : await ReadErrorAsync(response, ct);
        return new(null, error, (int)response.StatusCode);
    }

    /// <summary>LOT Track-In은 서버 라우팅 interlock을 통과한 경우에만 상태를 변경합니다.</summary>
    public Task<PomRoutingApiResult<PomLotDto>> ExecutePomLotTrackInAsync(
        string lotId, PomLotTrackInRequest request, CancellationToken ct = default)
        => SendPomRoutingAsync<PomLotDto>(
            HttpMethod.Post, PomLotUrl(lotId, "track-in"), request, ct);

    /// <summary>LOT Track-Out 결과와 품질/동시성 차단 사유를 HTTP 상태와 함께 보존합니다.</summary>
    public Task<PomRoutingApiResult<PomLotDto>> ExecutePomLotTrackOutAsync(
        string lotId, PomLotTrackOutRequest request, CancellationToken ct = default)
        => SendPomRoutingAsync<PomLotDto>(
            HttpMethod.Post, PomLotUrl(lotId, "track-out"), request, ct);

    public Task<PomRoutingApiResult<PomLotRoutingContextDto>> GetPomLotRoutingContextAsync(
        string lotId, CancellationToken ct = default)
        => SendPomRoutingAsync<PomLotRoutingContextDto>(
            HttpMethod.Get, PomLotUrl(lotId, "routing-context"), null, ct);

    public Task<PomRoutingApiResult<PomRoutingPolicyDecisionDto>> EvaluatePomLotRoutingAsync(
        string lotId, PomEvaluateRoutingRequest request, CancellationToken ct = default)
        => SendPomRoutingAsync<PomRoutingPolicyDecisionDto>(
            HttpMethod.Post, PomLotUrl(lotId, "routing/evaluate"), request, ct);

    public Task<PomRoutingApiResult<PomLotDto>> ChangePomLotRoutingControlModeAsync(
        string lotId, PomChangeRoutingControlModeRequest request, CancellationToken ct = default)
        => SendPomRoutingAsync<PomLotDto>(
            HttpMethod.Post, PomLotUrl(lotId, "routing/control-mode"), request, ct);

    public Task<PomRoutingApiResult<PomLotDto>> ApplyPomLotRouteDeviationAsync(
        string lotId, PomApplyRouteDeviationRequest request, CancellationToken ct = default)
        => SendPomRoutingAsync<PomLotDto>(
            HttpMethod.Post, PomLotUrl(lotId, "routing/deviations"), request, ct);

    public Task<PomRoutingApiResult<PomRouteExceptionDto>> RequestPomLotRouteExceptionAsync(
        string lotId, PomRequestRouteExceptionRequest request, CancellationToken ct = default)
        => SendPomRoutingAsync<PomRouteExceptionDto>(
            HttpMethod.Post, PomLotUrl(lotId, "routing/exceptions"), request, ct);

    public Task<PomRoutingApiResult<PomRouteExceptionDto>> ReviewPomLotRouteExceptionAsync(
        string action, string exceptionId, PomReviewRouteExceptionRequest request, CancellationToken ct = default)
    {
        var normalizedAction = action?.Trim().ToLowerInvariant();
        if (normalizedAction is not ("approve" or "reject"))
            return Task.FromResult(new PomRoutingApiResult<PomRouteExceptionDto>(
                null, $"지원하지 않는 라우팅 예외 검토 작업입니다: {action}", 400));
        if (string.IsNullOrWhiteSpace(exceptionId))
            return Task.FromResult(new PomRoutingApiResult<PomRouteExceptionDto>(
                null, "라우팅 예외 ID가 필요합니다.", 400));

        var id = Uri.EscapeDataString(exceptionId.Trim());
        return SendPomRoutingAsync<PomRouteExceptionDto>(
            HttpMethod.Post, $"api/v1/pom/routing/exceptions/{id}/{normalizedAction}", request, ct);
    }

    /// <summary>라우팅 API의 성공 DTO 또는 서버 오류와 상태 코드를 동일한 방식으로 읽습니다.</summary>
    private async Task<PomRoutingApiResult<T>> SendPomRoutingAsync<T>(
        HttpMethod method, string url, object? body, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(method, url, body, ct, surfaceErrors: false);
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(ct);
                return value is not null
                    ? new(value, null, (int)response.StatusCode)
                    : new(null, "라우팅 응답을 읽을 수 없습니다.", (int)response.StatusCode);
            }
            catch
            {
                return new(null, "라우팅 응답을 읽을 수 없습니다.", (int)response.StatusCode);
            }
        }

        var error = response.StatusCode == HttpStatusCode.Unauthorized
            ? "인증이 만료되었습니다. 다시 로그인해 주세요."
            : await ReadErrorAsync(response, ct);
        return new(null, error, (int)response.StatusCode);
    }

    private static string PomLotUrl(string lotId, string suffix)
        => string.IsNullOrWhiteSpace(lotId)
            ? "api/v1/pom/lots/_invalid_"
            : $"api/v1/pom/lots/{Uri.EscapeDataString(lotId.Trim())}/{suffix}";

    public Task<List<MaintenancePlanDto>> GetMaintenancePlansAsync(string? equipmentId = null, CancellationToken ct = default)
    {
        var url = "api/v1/ems/maintenance-plans" +
            (string.IsNullOrEmpty(equipmentId) ? "" : $"?equipmentId={equipmentId}");
        return GetListAsync<MaintenancePlanDto>(url, ct);
    }

    public Task<MaintenancePlanDto?> CreateMaintenancePlanAsync(object req, CancellationToken ct = default)
        => PostAsync<MaintenancePlanDto>("api/v1/ems/maintenance-plans", req, ct);

    public Task StartMaintenancePlanAsync(string planId, CancellationToken ct = default)
        => PostAsync($"api/v1/ems/maintenance-plans/{planId}/start", null, ct);

    public Task CompleteMaintenancePlanAsync(string planId, CancellationToken ct = default)
        => PostAsync($"api/v1/ems/maintenance-plans/{planId}/complete", null, ct);

    public Task CancelMaintenancePlanAsync(string planId, CancellationToken ct = default)
        => PostAsync($"api/v1/ems/maintenance-plans/{planId}/cancel", null, ct);

    public Task<List<SparePartDto>> GetSparePartsAsync(bool lowStock = false, CancellationToken ct = default)
        => GetListAsync<SparePartDto>($"api/v1/ems/spare-parts{(lowStock ? "?lowStock=true" : "")}", ct);

    public Task<SparePartDto?> CreateSparePartAsync(object req, CancellationToken ct = default)
        => PostAsync<SparePartDto>("api/v1/ems/spare-parts", req, ct);

    public Task AdjustStockAsync(string partId, decimal delta, CancellationToken ct = default)
        => PostAsync($"api/v1/ems/spare-parts/{partId}/adjust-stock", new { delta }, ct);

    // ── PPM ───────────────────────────────────────────────────────────────────

    public Task<List<ProductionPlanDto>> GetPlansAsync(string plantId, CancellationToken ct = default)
        => GetListAsync<ProductionPlanDto>($"api/v1/pom/plans?plantId={plantId}", ct);

    public Task<ProductionPlanDto?> CreatePlanAsync(object req, CancellationToken ct = default)
        => PostAsync<ProductionPlanDto>("api/v1/pom/plans", req, ct);

    public Task StartPlanAsync(string planId, CancellationToken ct = default)
        => PostAsync($"api/v1/pom/plans/{planId}/start", null, ct);

    public Task ReleasePlanAsync(string planId, CancellationToken ct = default)
        => PostAsync($"api/v1/pom/plans/{planId}/release", null, ct);

    public Task CompletePlanAsync(string planId, CancellationToken ct = default)
        => PostAsync($"api/v1/pom/plans/{planId}/complete", null, ct);

    public Task CancelPlanAsync(string planId, CancellationToken ct = default)
        => PostAsync($"api/v1/pom/plans/{planId}/cancel", null, ct);

    public Task<List<ProductionOrderDto>> GetOrdersAsync(string planId, CancellationToken ct = default)
        => GetListAsync<ProductionOrderDto>($"api/v1/pom/orders?planId={planId}", ct);

    public Task<ProductionOrderDto?> CreateOrderAsync(object req, CancellationToken ct = default)
        => PostAsync<ProductionOrderDto>("api/v1/pom/orders", req, ct);

    public Task StartOrderAsync(string orderId, CancellationToken ct = default)
        => PostAsync($"api/v1/pom/orders/{orderId}/start", null, ct);

    public Task CompleteOrderAsync(string orderId, decimal actualQty, CancellationToken ct = default)
        => PostAsync($"api/v1/pom/orders/{orderId}/complete", new { actualQty }, ct);

    public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        => PostAsync($"api/v1/pom/orders/{orderId}/cancel", null, ct);

    // ── PPM - Lot TrackIn/TrackOut (설계서 19.4) ──────────────────────────────
    // Lot 조회(목록/경로/추적 리포트)는 명명 쿼리 게이트웨이(/api/v1/query/POM.*)가 단일 경로다 — 구 REST 조회는 삭제.

    public Task<(LotDto? Lot, string? Error)> CreateLotAsync(object req, CancellationToken ct = default)
        => PostWithErrorAsync<LotDto>("api/v1/pom/lots", req, ct);

    public Task<(LotDto? Lot, string? Error)> TrackInAsync(string lotId, object req, CancellationToken ct = default)
        => PostWithErrorAsync<LotDto>($"api/v1/pom/lots/{Uri.EscapeDataString(lotId)}/track-in", req, ct);

    public Task<(LotDto? Lot, string? Error)> TrackOutAsync(string lotId, object req, CancellationToken ct = default)
        => PostWithErrorAsync<LotDto>($"api/v1/pom/lots/{Uri.EscapeDataString(lotId)}/track-out", req, ct);

    public Task<(LotDto? Lot, string? Error)> MixingTrackInOutAsync(object req, CancellationToken ct = default)
        => PostWithErrorAsync<LotDto>("api/v1/pom/lots/mixing/track-in-out", req, ct);

    public Task<bool> HoldLotAsync(string lotId, CancellationToken ct = default)
        => HoldLotAsync(lotId, new PomLotHoldRequest(), ct);

    public Task<bool> HoldLotAsync(string lotId, PomLotHoldRequest request, CancellationToken ct = default)
        => PostForStatusAsync(PomLotHoldUri(lotId, "hold", request), null, ct);

    public Task<bool> ReleaseLotHoldAsync(string lotId, CancellationToken ct = default)
        => ReleaseLotHoldAsync(lotId, new PomLotHoldRequest(), ct);

    public Task<bool> ReleaseLotHoldAsync(string lotId, PomLotHoldRequest request, CancellationToken ct = default)
        => PostForStatusAsync(PomLotHoldUri(lotId, "release", request), null, ct);

    private static string PomLotHoldUri(string lotId, string action, PomLotHoldRequest request)
    {
        var query = new List<string>
        {
            $"clientChannel={Uri.EscapeDataString(request.ClientChannel)}",
        };
        if (request.ExpectedVersion is int version)
            query.Add($"expectedVersion={version.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            query.Add($"idempotencyKey={Uri.EscapeDataString(request.IdempotencyKey)}");
        if (!string.IsNullOrWhiteSpace(request.Reason))
            query.Add($"reason={Uri.EscapeDataString(request.Reason)}");
        if (!string.IsNullOrWhiteSpace(request.DeviceId))
            query.Add($"deviceId={Uri.EscapeDataString(request.DeviceId)}");
        return $"api/v1/pom/lots/{Uri.EscapeDataString(lotId)}/{action}?{string.Join('&', query)}";
    }

    // ── DLV ───────────────────────────────────────────────────────────────────
    // 출하 조회(오더 목록/품목/이력)는 명명 쿼리 게이트웨이(/api/v1/query/SHP.*)가 단일 경로 — 브리지는 전이 쓰기만.

    public Task<DeliveryOrderDto?> CreateDeliveryOrderAsync(object req, CancellationToken ct = default)
        => PostAsync<DeliveryOrderDto>("api/v1/shp/orders", req, ct);

    public Task ConfirmDeliveryOrderAsync(string orderId, CancellationToken ct = default)
        => PostAsync($"api/v1/shp/orders/{orderId}/confirm", null, ct);

    public Task ShipDeliveryOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default)
        => PostAsync($"api/v1/shp/orders/{orderId}/ship", new { shippedDate }, ct);

    public Task CancelDeliveryOrderAsync(string orderId, CancellationToken ct = default)
        => PostAsync($"api/v1/shp/orders/{orderId}/cancel", null, ct);

    // ── SYS ───────────────────────────────────────────────────────────────────
    // 사용자/역할 조회는 명명 쿼리(SYS.ListUsers/ListRoles 등), 쓰기는 sys/admin 브리지가 단일 경로다.
    // 잠금 해제(unlock)는 인증 경로 소유(S7)로 통합 호스트 REST가 없다 — 필요 시 인증 경로에 신설한다.

    public Task DeactivateUserAsync(string userId, CancellationToken ct = default)
        => PostAsync($"api/v1/sys/admin/users/{userId}/deactivate", null, ct);

    // §20.10 — 관리자 잠금 해제(인증 경로 소유 — auth 라우트)
    public Task UnlockUserAsync(string userId, CancellationToken ct = default)
        => PostAsync($"api/v1/auth/users/{Uri.EscapeDataString(userId)}/unlock", null, ct);

    public Task<RoleDto?> CreateRoleAsync(object req, CancellationToken ct = default)
        => PostAsync<RoleDto>("api/v1/sys/admin/roles", req, ct);

    public Task AddPermissionAsync(string roleId, string permission, CancellationToken ct = default)
        => PostAsync($"api/v1/sys/admin/roles/{roleId}/permissions", new { permission }, ct);

    public Task RemovePermissionAsync(string roleId, string permission, CancellationToken ct = default)
        => DeleteWithBodyAsync($"api/v1/sys/admin/roles/{roleId}/permissions", new { permission }, ct);

    // FDC 가상 이벤트 수동 평가(브리지, fdc:manage) — 워커 주기를 기다리지 않고 즉시 판정.
    public Task<VirtualEventEvaluationDto?> EvaluateVirtualEventAsync(string equipmentId, string eventId, CancellationToken ct = default)
        => PostAsync<VirtualEventEvaluationDto>(
            $"api/v1/fdc/virtual-events/{Uri.EscapeDataString(equipmentId)}/{Uri.EscapeDataString(eventId)}/evaluate", new { }, ct);

    // ── SYS - 사용자 메뉴 개인화 (설계서 20.12 즐겨찾기/최근 메뉴) ────────────
    // 호스트 SysPersonalizationController — 자기 데이터만(토큰 사용자 스코프), 권한 요구 없음(인증만).

    // 사용자 언어(P3-14) — 토큰 사용자 스코프. 리소스는 명명 쿼리로 읽는다(언어별 공통 문구, @currentUser 불요).
    public async Task<string> GetUserLanguageAsync(CancellationToken ct = default)
        => (await GetAsync<UserLanguageDto>("api/v1/sys/language", ct))?.Language ?? "KoKr";

    public Task<bool> SetUserLanguageAsync(string language, CancellationToken ct = default)
        => PutForStatusAsync("api/v1/sys/language", new { language }, ct);

    public async Task<Dictionary<string, string>> GetLanguageResourcesAsync(string language, CancellationToken ct = default)
    {
        var rows = await ExecuteQueryAsync("SYS.LanguageResources", new { language }, ct);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row.TryGetValue("RESOURCE_KEY", out var k) && k is not null)
                map[k.ToString()!] = row.TryGetValue("VALUE", out var v) ? v?.ToString() ?? "" : "";
        return map;
    }

    // 역할 필터 메뉴 트리 — SYS.MenuTreeForUser(@currentUser)를 호스트가 토큰 사용자로 바인딩해 실행한다.
    // 행 형태는 ExecuteQueryAsync와 동일(컬럼명 키 딕셔너리)이라 셸 ToNode 매핑을 그대로 쓴다.
    public Task<List<Dictionary<string, object?>>> GetMenuTreeAsync(CancellationToken ct = default)
        => GetListAsync<Dictionary<string, object?>>("api/v1/sys/menu-tree", ct);

    public Task<List<FavoriteMenuDto>> GetFavoriteMenusAsync(CancellationToken ct = default)
        => GetListAsync<FavoriteMenuDto>("api/v1/sys/favorites", ct);

    public Task<bool> AddFavoriteMenuAsync(string menuId, CancellationToken ct = default)
        => PostForStatusAsync("api/v1/sys/favorites", new { menuId }, ct);

    public Task<bool> RemoveFavoriteMenuAsync(string menuId, CancellationToken ct = default)
        => DeleteAsync($"api/v1/sys/favorites?menuId={Uri.EscapeDataString(menuId)}", ct);

    public Task<bool> ReorderFavoriteMenusAsync(List<string> menuIds, CancellationToken ct = default)
        => PutForStatusAsync("api/v1/sys/favorites/order", new { menuIds }, ct);

    public Task<List<RecentMenuDto>> GetRecentMenusAsync(CancellationToken ct = default)
        => GetListAsync<RecentMenuDto>("api/v1/sys/recent-menus", ct);

    public Task<bool> RecordRecentMenuAsync(string menuId, CancellationToken ct = default)
        => PostForStatusAsync("api/v1/sys/recent-menus", new { menuId }, ct);

    // ── SYS - Deploy (설계서 20.11 배포 파일 업로드/클라이언트 업데이트) ──────
    // 관리(목록/업로드/활성 전환)=sys:manage, 소비(latest)=인증만. 다운로드는 files/{id}/download 규약.

    public Task<List<DeployFileDto>> GetDeployFilesAsync(CancellationToken ct = default)
        => GetListAsync<DeployFileDto>("api/v1/deploy/files", ct);

    public Task<DeployFileDto?> GetLatestDeployAsync(CancellationToken ct = default)
        => GetAsync<DeployFileDto>("api/v1/deploy/latest", ct);

    public async Task<(DeployFileDto? File, string? Error)> UploadDeployFileAsync(
        Stream content, string fileName, string version, string description, bool forceUpdate,
        CancellationToken ct = default)
    {
        // InputFile 스트림은 되감기가 불가능해 401 재시도 없이 1회만 전송한다 —
        // 만료 임박 토큰을 선제 갱신(GetValidAccessTokenAsync)하므로 전송 중 만료 경합은 드물다.
        var token = await GetValidAccessTokenAsync(ct);

        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(version), "version");
        form.Add(new StringContent(description), "description");
        form.Add(new StringContent(forceUpdate ? "true" : "false"), "forceUpdate");

        // LongRunning 표시 — DefaultRequestTimeoutHandler의 100초 제한을 건너뛰고 전역 10분 한도만 적용
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/deploy/files") { Content = form };
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Options.Set(DefaultRequestTimeoutHandler.LongRunning, true);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, _ui.T("error.unreachable", "서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요."));
        }
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<DeployFileDto>(ct), null);

        try
        {
            // 서버 BadRequest 본문(Error)의 한국어 사유를 그대로 보여준다 (버전 중복/형식 오류 등 — 서버측 다국어는 별도 아크)
            var error = await resp.Content.ReadFromJsonAsync<ApiErrorPayload>(ct);
            if (!string.IsNullOrEmpty(error?.Description))
                return (null, error.Description);
        }
        catch { /* 오류 본문이 표준 형식이 아니면 상태 코드로 폴백 */ }
        return (null, string.Format(_ui.T("error.uploadFailed", "업로드에 실패했습니다 (HTTP {0})."), (int)resp.StatusCode));
    }

    public Task<bool> SetDeployFileActiveAsync(string fileId, bool isActive, CancellationToken ct = default)
        => PostForStatusAsync($"api/v1/deploy/files/{Uri.EscapeDataString(fileId)}/{(isActive ? "activate" : "deactivate")}", null, ct);

    // ── SYS - ConditionSetting (설계서 20.8 조건 저장/불러오기) ───────────────
    // 호스트 SysPersonalizationController — 토큰 사용자 스코프. '$latest'=마지막 조회 조건(자동 저장).

    public Task<ConditionSettingDto?> GetConditionSettingsAsync(string menuId, CancellationToken ct = default)
        => GetAsync<ConditionSettingDto>($"api/v1/sys/conditions?menuId={Uri.EscapeDataString(menuId)}", ct);

    public Task<ConditionItemDto?> SaveConditionAsync(string menuId, string name, Dictionary<string, string?> values, CancellationToken ct = default)
        => PostAsync<ConditionItemDto>("api/v1/sys/conditions", new { menuId, name, values }, ct);

    public async Task<bool> SaveLatestConditionAsync(string menuId, Dictionary<string, string?> values, CancellationToken ct = default)
        => await PostAsync<ConditionItemDto>("api/v1/sys/conditions/latest", new { menuId, values }, ct) is not null;

    public Task<bool> DeleteConditionAsync(string menuId, string name, CancellationToken ct = default)
        => DeleteAsync($"api/v1/sys/conditions?menuId={Uri.EscapeDataString(menuId)}&name={Uri.EscapeDataString(name)}", ct);

    public Task<bool> ClearLatestConditionAsync(string menuId, CancellationToken ct = default)
        => DeleteAsync($"api/v1/sys/conditions/latest?menuId={Uri.EscapeDataString(menuId)}", ct);

    // ── SYS - 사용자 등록 신청/승인 (설계서 19.3) ─────────────────────────────

    private sealed record UserIdAvailabilityPayload(bool Available);

    // 중복확인/신청은 로그인 전 화면이라 토큰 없이 직접 호출한다 (LoginAsync와 동일)
    public async Task<bool?> CheckUserIdAvailableAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync(
                $"api/v1/sys/admin/user-requests/availability?userId={Uri.EscapeDataString(userId)}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var payload = await resp.Content.ReadFromJsonAsync<UserIdAvailabilityPayload>(ct);
            return payload?.Available;
        }
        catch { return null; }
    }

    public async Task<(UserRequestDto? Request, string? Error)> RegisterUserAsync(
        object req, CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync("api/v1/sys/admin/user-requests", req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, "서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요.");
        }
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<UserRequestDto>(ct), null);

        try
        {
            var error = await resp.Content.ReadFromJsonAsync<ApiErrorPayload>(ct);
            if (!string.IsNullOrEmpty(error?.Description))
                return (null, error.Description);
        }
        catch { /* 오류 본문이 표준 형식이 아니면 상태 코드로 폴백 */ }
        return (null, $"신청에 실패했습니다 (HTTP {(int)resp.StatusCode}).");
    }

    public Task<List<UserRequestDto>> GetUserRequestsAsync(
        string? plantId = null, string? status = null, string? userId = null,
        string? userName = null, string? email = null,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(plantId)) qs.Add($"plantId={Uri.EscapeDataString(plantId)}");
        if (!string.IsNullOrEmpty(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrEmpty(userId)) qs.Add($"userId={Uri.EscapeDataString(userId)}");
        if (!string.IsNullOrEmpty(userName)) qs.Add($"userName={Uri.EscapeDataString(userName)}");
        if (!string.IsNullOrEmpty(email)) qs.Add($"email={Uri.EscapeDataString(email)}");
        if (from.HasValue) qs.Add($"from={from.Value:O}");
        if (to.HasValue) qs.Add($"to={to.Value:O}");
        var url = "api/v1/sys/admin/user-requests" + (qs.Any() ? "?" + string.Join("&", qs) : "");
        return GetListAsync<UserRequestDto>(url, ct);
    }

    public Task<(UserRequestApprovalDto? Approval, string? Error)> ApproveUserRequestAsync(
        string requestId, string? roleId, CancellationToken ct = default)
        => PostWithErrorAsync<UserRequestApprovalDto>(
            $"api/v1/sys/admin/user-requests/{Uri.EscapeDataString(requestId)}/approve", new { roleId }, ct);

    public Task<(UserRequestDto? Request, string? Error)> RejectUserRequestAsync(
        string requestId, string reason, CancellationToken ct = default)
        => PostWithErrorAsync<UserRequestDto>(
            $"api/v1/sys/admin/user-requests/{Uri.EscapeDataString(requestId)}/reject", new { reason }, ct);
}
