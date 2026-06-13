using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NexaOne.Web.Services.Auth;

namespace NexaOne.Web.Services.Api;

public sealed class ApiClient : IApiClient
{
    private readonly HttpClient _http;
    private readonly AuthTokenService _tokenService;
    private readonly JwtAuthStateProvider _authState;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private static readonly JwtSecurityTokenHandler _jwtHandler = new();

    public ApiClient(HttpClient http, AuthTokenService tokenService, JwtAuthStateProvider authState)
    {
        _http = http;
        _tokenService = tokenService;
        _authState = authState;
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
    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string url, object? body, string? token, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return await _http.SendAsync(req, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var token = await GetValidAccessTokenAsync(ct);
        var resp = await SendOnceAsync(method, url, body, token, ct);
        if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;

        resp.Dispose();                       // 첫 401 응답 소켓/리소스 해제(누수 방지)
        var refreshed = await RefreshAsync(ct);
        return await SendOnceAsync(method, url, body, refreshed, ct);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var error = await resp.Content.ReadFromJsonAsync<ApiErrorPayload>(ct);
            if (!string.IsNullOrEmpty(error?.Description))
                return error.Description;
        }
        catch { /* 오류 본문이 표준 형식이 아니면 상태 코드로 폴백 */ }
        return $"요청에 실패했습니다 (HTTP {(int)resp.StatusCode}).";
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

    private async Task<bool> DeleteAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Delete, url, null, ct);
        return resp.IsSuccessStatusCode;
    }

    // §19.4 — 검증 실패 사유(Error.Description)를 화면에 보여줘야 하는 POST용
    private async Task<(T? Result, string? Error)> PostWithErrorAsync<T>(
        string url, object body, CancellationToken ct) where T : class
    {
        using var resp = await SendAsync(HttpMethod.Post, url, body, ct);
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<T>(ct), null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return (null, "인증이 만료되었습니다. 다시 로그인해 주세요.");
        return (null, await ReadErrorAsync(resp, ct));
    }

    // §20.12 — 본문 없는 204 응답의 성공 여부가 필요한 POST용 (PostAsync<T>는 본문 역직렬화 전제)
    private async Task<bool> PostForStatusAsync(string url, object body, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Post, url, body, ct);
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
        using var resp = await SendAsync(HttpMethod.Patch, url, body, ct);
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

    // ── Dashboard ─────────────────────────────────────────────────────────────

    public Task<DashboardSummaryDto?> GetDashboardAsync(CancellationToken ct = default)
        => GetAsync<DashboardSummaryDto>("api/v1/dashboard", ct);

    // ── Auth ──────────────────────────────────────────────────────────────────

    public async Task<LoginResult> LoginAsync(string userId, string password, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/login",
            new { userId, password, plantId = "DEFAULT" }, ct);
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
        => await _http.PostAsJsonAsync("api/v1/auth/forgot-password", new { userId, email }, ct);

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Post, "api/v1/auth/change-password",
            new { currentPassword, newPassword, confirmPassword }, ct);
        resp.EnsureSuccessStatusCode();

        // §20.10 — 변경 성공 시 서버가 pwdChange 클레임 없는 새 토큰을 재발급한다.
        // 이전 토큰은 만료까지 업무 API가 차단되므로 즉시 교체한다.
        var payload = await resp.Content.ReadFromJsonAsync<RefreshTokenPayload>(ct);
        var userId = await _tokenService.GetUserIdAsync();
        if (payload is not null && userId is not null)
        {
            await _tokenService.SaveAsync(payload.AccessToken, payload.RefreshToken, userId);
            _authState.NotifyAuthChanged(null);   // 새 토큰 기준으로 인증 상태 재평가
        }
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
        => GetListAsync<EquipmentStateMatrixDto>($"api/v1/ept/state-matrix?plantId={plantId}", ct);

    public Task<List<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(string plantId, string fromState, CancellationToken ct = default)
        => GetListAsync<EquipmentStateMatrixDto>($"api/v1/ept/state-matrix/allowed?plantId={plantId}&fromState={fromState}", ct);

    public Task<EquipmentStateMatrixDto?> UpsertStateMatrixAsync(object req, CancellationToken ct = default)
        => PostAsync<EquipmentStateMatrixDto>("api/v1/ept/state-matrix", req, ct);

    // 실패(null)와 빈 결과를 구분한다 — 폴백/병합 갱신 실패가 표시 중인 그리드를 비우지 않도록 (§20.9)
    public Task<List<EquipmentCurrentStateDto>?> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default)
        => GetAsync<List<EquipmentCurrentStateDto>>($"api/v1/ept/equipment-state?plantId={plantId}", ct);

    public Task<EquipmentCurrentStateDto?> ChangeEquipmentStateAsync(object req, CancellationToken ct = default)
        => PostAsync<EquipmentCurrentStateDto>("api/v1/ept/equipment-state/change", req, ct);

    public Task<List<EquipmentStateHistoryDto>> GetStateHistoryAsync(string equipmentId, CancellationToken ct = default)
        => GetListAsync<EquipmentStateHistoryDto>($"api/v1/ept/equipment-state/{equipmentId}/history", ct);

    // 실패(null)와 빈 결과를 구분한다 — 폴백/병합 갱신 실패가 표시 중인 그리드를 비우지 않도록 (§20.9)
    public Task<List<AlarmDto>?> GetAlarmsAsync(string plantId, CancellationToken ct = default)
        => GetAsync<List<AlarmDto>>($"api/v1/ept/alarms?plantId={plantId}", ct);

    public Task ClearAlarmAsync(string alarmId, CancellationToken ct = default)
        => PutAsync($"api/v1/ept/alarms/{alarmId}/clear", new { ClearedAt = DateTime.UtcNow }, ct);

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

    // Phase 4 후속 — Low-Code 화면 정의 저장소
    public Task<List<ScreenDefinitionRecordDto>> GetScreenDefinitionsAsync(CancellationToken ct = default)
        => GetListAsync<ScreenDefinitionRecordDto>("api/v1/sys/screen-definitions", ct);

    public Task<ScreenDefinitionRecordDto?> GetScreenDefinitionAsync(string uiId, CancellationToken ct = default)
        => GetAsync<ScreenDefinitionRecordDto>($"api/v1/sys/screen-definitions/{uiId}", ct);

    public Task SaveScreenDefinitionAsync(string uiId, string title, string definitionJson, CancellationToken ct = default)
        => PutAsync($"api/v1/sys/screen-definitions/{uiId}", new { title, definitionJson }, ct);

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
        => PutAsync($"api/v1/qms/defects/{defectId}/confirm", new { confirmerId }, ct);

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

    public Task<List<SpcParamDto>> GetSpcParamsAsync(string equipmentId, CancellationToken ct = default)
        => GetListAsync<SpcParamDto>($"api/v1/qms/spc-params?equipmentId={equipmentId}", ct);

    public Task<SpcParamDto?> CreateSpcParamAsync(object req, CancellationToken ct = default)
        => PostAsync<SpcParamDto>("api/v1/qms/spc-params", req, ct);

    public Task UpdateSpcLimitsAsync(string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default)
        => PutAsync($"api/v1/qms/spc-params/{paramId}/limits", new { mean, ucl, lcl }, ct);

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
        => PutAsync($"api/v1/ems/work-orders/{woId}/start", null, ct);

    public Task CompleteWorkOrderAsync(string woId, string remark, CancellationToken ct = default)
        => PutAsync($"api/v1/ems/work-orders/{woId}/complete", new { remark }, ct);

    public Task CancelWorkOrderAsync(string woId, CancellationToken ct = default)
        => PutAsync($"api/v1/ems/work-orders/{woId}/cancel", null, ct);

    public Task<List<MaintenancePlanDto>> GetMaintenancePlansAsync(string? equipmentId = null, CancellationToken ct = default)
    {
        var url = "api/v1/ems/maintenance-plans" +
            (string.IsNullOrEmpty(equipmentId) ? "" : $"?equipmentId={equipmentId}");
        return GetListAsync<MaintenancePlanDto>(url, ct);
    }

    public Task<MaintenancePlanDto?> CreateMaintenancePlanAsync(object req, CancellationToken ct = default)
        => PostAsync<MaintenancePlanDto>("api/v1/ems/maintenance-plans", req, ct);

    public Task StartMaintenancePlanAsync(string planId, CancellationToken ct = default)
        => PutAsync($"api/v1/ems/maintenance-plans/{planId}/start", null, ct);

    public Task CompleteMaintenancePlanAsync(string planId, CancellationToken ct = default)
        => PutAsync($"api/v1/ems/maintenance-plans/{planId}/complete", null, ct);

    public Task CancelMaintenancePlanAsync(string planId, CancellationToken ct = default)
        => PutAsync($"api/v1/ems/maintenance-plans/{planId}/cancel", null, ct);

    public Task<List<SparePartDto>> GetSparePartsAsync(bool lowStock = false, CancellationToken ct = default)
        => GetListAsync<SparePartDto>($"api/v1/ems/spare-parts{(lowStock ? "?lowStock=true" : "")}", ct);

    public Task<SparePartDto?> CreateSparePartAsync(object req, CancellationToken ct = default)
        => PostAsync<SparePartDto>("api/v1/ems/spare-parts", req, ct);

    public Task AdjustStockAsync(string partId, decimal delta, CancellationToken ct = default)
        => PutAsync($"api/v1/ems/spare-parts/{partId}/adjust-stock", new { delta }, ct);

    // ── PPM ───────────────────────────────────────────────────────────────────

    public Task<List<ProductionPlanDto>> GetPlansAsync(string plantId, CancellationToken ct = default)
        => GetListAsync<ProductionPlanDto>($"api/v1/ppm/plans?plantId={plantId}", ct);

    public Task<ProductionPlanDto?> CreatePlanAsync(object req, CancellationToken ct = default)
        => PostAsync<ProductionPlanDto>("api/v1/ppm/plans", req, ct);

    public Task StartPlanAsync(string planId, CancellationToken ct = default)
        => PutAsync($"api/v1/ppm/plans/{planId}/start", null, ct);

    public Task ReleasePlanAsync(string planId, CancellationToken ct = default)
        => PutAsync($"api/v1/ppm/plans/{planId}/release", null, ct);

    public Task CompletePlanAsync(string planId, CancellationToken ct = default)
        => PutAsync($"api/v1/ppm/plans/{planId}/complete", null, ct);

    public Task CancelPlanAsync(string planId, CancellationToken ct = default)
        => PutAsync($"api/v1/ppm/plans/{planId}/cancel", null, ct);

    public Task<List<ProductionOrderDto>> GetOrdersAsync(string planId, CancellationToken ct = default)
        => GetListAsync<ProductionOrderDto>($"api/v1/ppm/orders?planId={planId}", ct);

    public Task<ProductionOrderDto?> CreateOrderAsync(object req, CancellationToken ct = default)
        => PostAsync<ProductionOrderDto>("api/v1/ppm/orders", req, ct);

    public Task StartOrderAsync(string orderId, CancellationToken ct = default)
        => PutAsync($"api/v1/ppm/orders/{orderId}/start", null, ct);

    public Task CompleteOrderAsync(string orderId, decimal actualQty, CancellationToken ct = default)
        => PutAsync($"api/v1/ppm/orders/{orderId}/complete", new { actualQty }, ct);

    public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        => PutAsync($"api/v1/ppm/orders/{orderId}/cancel", null, ct);

    // ── PPM - Lot TrackIn/TrackOut (설계서 19.4) ──────────────────────────────

    public Task<List<LotDto>> GetLotsAsync(string plantId, string? state = null, CancellationToken ct = default)
        => GetListAsync<LotDto>(
               $"api/v1/lots?plantId={Uri.EscapeDataString(plantId)}{(string.IsNullOrEmpty(state) ? "" : $"&state={Uri.EscapeDataString(state)}")}", ct);

    public Task<LotRouteDto?> GetLotRouteAsync(string lotId, CancellationToken ct = default)
        => GetAsync<LotRouteDto>($"api/v1/lots/{Uri.EscapeDataString(lotId)}/route", ct);

    public Task<(LotDto? Lot, string? Error)> CreateLotAsync(object req, CancellationToken ct = default)
        => PostWithErrorAsync<LotDto>("api/v1/lots", req, ct);

    public Task<(LotDto? Lot, string? Error)> TrackInAsync(string lotId, object req, CancellationToken ct = default)
        => PostWithErrorAsync<LotDto>($"api/v1/lots/{Uri.EscapeDataString(lotId)}/track-in", req, ct);

    public Task<(LotDto? Lot, string? Error)> TrackOutAsync(string lotId, object req, CancellationToken ct = default)
        => PostWithErrorAsync<LotDto>($"api/v1/lots/{Uri.EscapeDataString(lotId)}/track-out", req, ct);

    public Task<(LotDto? Lot, string? Error)> MixingTrackInOutAsync(object req, CancellationToken ct = default)
        => PostWithErrorAsync<LotDto>("api/v1/lots/mixing/track-in-out", req, ct);

    public Task<bool> HoldLotAsync(string lotId, CancellationToken ct = default)
        => PutForStatusAsync($"api/v1/lots/{Uri.EscapeDataString(lotId)}/hold", null, ct);

    public Task<bool> ReleaseLotHoldAsync(string lotId, CancellationToken ct = default)
        => PutForStatusAsync($"api/v1/lots/{Uri.EscapeDataString(lotId)}/release-hold", null, ct);

    public Task<List<LotHistoryDto>> GetLotTrackingReportAsync(
        string plantId, string? lotId = null, string? equipmentId = null, string? processId = null,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var query = $"api/v1/reports/lot-tracking?plantId={Uri.EscapeDataString(plantId)}";
        if (!string.IsNullOrEmpty(lotId)) query += $"&lotId={Uri.EscapeDataString(lotId)}";
        if (!string.IsNullOrEmpty(equipmentId)) query += $"&equipmentId={Uri.EscapeDataString(equipmentId)}";
        if (!string.IsNullOrEmpty(processId)) query += $"&processId={Uri.EscapeDataString(processId)}";
        if (from.HasValue) query += $"&from={from.Value:O}";
        if (to.HasValue) query += $"&to={to.Value:O}";
        return GetListAsync<LotHistoryDto>(query, ct);
    }

    // ── DLV ───────────────────────────────────────────────────────────────────

    public Task<List<DeliveryOrderDto>> GetDeliveryOrdersAsync(string plantId, CancellationToken ct = default)
        => GetListAsync<DeliveryOrderDto>($"api/v1/dlv/orders?plantId={plantId}", ct);

    public Task<DeliveryOrderDto?> CreateDeliveryOrderAsync(object req, CancellationToken ct = default)
        => PostAsync<DeliveryOrderDto>("api/v1/dlv/orders", req, ct);

    public Task ConfirmDeliveryOrderAsync(string orderId, CancellationToken ct = default)
        => PutAsync($"api/v1/dlv/orders/{orderId}/confirm", null, ct);

    public Task ShipDeliveryOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default)
        => PutAsync($"api/v1/dlv/orders/{orderId}/ship", new { shippedDate }, ct);

    public Task CancelDeliveryOrderAsync(string orderId, CancellationToken ct = default)
        => PutAsync($"api/v1/dlv/orders/{orderId}/cancel", null, ct);

    public Task<List<DeliveryItemDto>> GetDeliveryItemsAsync(string orderId, CancellationToken ct = default)
        => GetListAsync<DeliveryItemDto>($"api/v1/dlv/orders/{orderId}/items", ct);

    public Task<DeliveryItemDto?> AddDeliveryItemAsync(string orderId, object req, CancellationToken ct = default)
        => PostAsync<DeliveryItemDto>($"api/v1/dlv/orders/{orderId}/items", req, ct);

    public Task SetDeliveryItemActualQtyAsync(string itemId, decimal actualQty, CancellationToken ct = default)
        => PutAsync($"api/v1/dlv/items/{itemId}/actual-qty", new { actualQty }, ct);

    public Task<List<ShipmentHistoryDto>> GetShipmentHistoryAsync(string orderId, CancellationToken ct = default)
        => GetListAsync<ShipmentHistoryDto>($"api/v1/dlv/orders/{orderId}/shipment-history", ct);

    public Task<ShipmentHistoryDto?> RecordShipmentAsync(string orderId, object req, CancellationToken ct = default)
        => PostAsync<ShipmentHistoryDto>($"api/v1/dlv/orders/{orderId}/shipment-history", req, ct);

    // ── SYS ───────────────────────────────────────────────────────────────────

    public Task<List<UserDto>> GetUsersAsync(CancellationToken ct = default)
        => GetListAsync<UserDto>("api/v1/sys/users", ct);

    public Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default)
        => GetAsync<UserDto>($"api/v1/sys/users/{userId}", ct);

    public Task<UserDto?> CreateUserAsync(object req, CancellationToken ct = default)
        => PostAsync<UserDto>("api/v1/sys/users", req, ct);

    public Task DeactivateUserAsync(string userId, CancellationToken ct = default)
        => PutAsync($"api/v1/sys/users/{userId}/deactivate", null, ct);

    // §20.10 — 관리자 잠금 해제
    public Task UnlockUserAsync(string userId, CancellationToken ct = default)
        => PutAsync($"api/v1/sys/users/{userId}/unlock", null, ct);

    public Task<List<RoleDto>> GetRolesAsync(CancellationToken ct = default)
        => GetListAsync<RoleDto>("api/v1/sys/roles", ct);

    public Task<RoleDto?> GetRoleAsync(string roleId, CancellationToken ct = default)
        => GetAsync<RoleDto>($"api/v1/sys/roles/{roleId}", ct);

    public Task<RoleDto?> CreateRoleAsync(object req, CancellationToken ct = default)
        => PostAsync<RoleDto>("api/v1/sys/roles", req, ct);

    public Task AddPermissionAsync(string roleId, string permission, CancellationToken ct = default)
        => PostAsync<object>($"api/v1/sys/roles/{roleId}/permissions", new { permission }, ct);

    public Task RemovePermissionAsync(string roleId, string permission, CancellationToken ct = default)
        => DeleteAsync($"api/v1/sys/roles/{roleId}/permissions/{Uri.EscapeDataString(permission)}", ct);

    public Task<List<MultiLanguageResourceDto>> GetLanguageResourcesAsync(string? menuId = null, string? language = null, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(menuId)) qs.Add($"menuId={menuId}");
        if (!string.IsNullOrEmpty(language)) qs.Add($"language={language}");
        var url = "api/v1/sys/languages" + (qs.Any() ? "?" + string.Join("&", qs) : "");
        return GetListAsync<MultiLanguageResourceDto>(url, ct);
    }

    public Task<MultiLanguageResourceDto?> UpsertLanguageResourceAsync(object req, CancellationToken ct = default)
        => PostAsync<MultiLanguageResourceDto>("api/v1/sys/languages", req, ct);

    public Task<List<MenuItemDto>> GetMenuAsync(CancellationToken ct = default)
        => GetListAsync<MenuItemDto>("api/v1/sys/menu", ct);

    // ── SYS - ConditionSetting (설계서 20.8 조건 저장/불러오기) ───────────────

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

    // ── SYS - Deploy (설계서 20.11 배포 파일 업로드/클라이언트 업데이트) ──────

    public Task<List<DeployFileDto>> GetDeployFilesAsync(CancellationToken ct = default)
        => GetListAsync<DeployFileDto>("api/v1/deploy/files", ct);

    public Task<DeployLatestDto?> GetLatestDeployAsync(CancellationToken ct = default)
        => GetAsync<DeployLatestDto>("api/v1/deploy/latest", ct);

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
        var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<DeployFileDto>(ct), null);

        try
        {
            // 서버 BadRequest 본문(Error)의 한국어 사유를 그대로 보여준다 (버전 중복/형식 오류 등)
            var error = await resp.Content.ReadFromJsonAsync<ApiErrorPayload>(ct);
            if (!string.IsNullOrEmpty(error?.Description))
                return (null, error.Description);
        }
        catch { /* 오류 본문이 표준 형식이 아니면 상태 코드로 폴백 */ }
        return (null, $"업로드에 실패했습니다 (HTTP {(int)resp.StatusCode}).");
    }

    public Task<bool> SetDeployFileActiveAsync(string fileId, bool isActive, CancellationToken ct = default)
        => PutForStatusAsync($"api/v1/deploy/files/{Uri.EscapeDataString(fileId)}/{(isActive ? "activate" : "deactivate")}", null, ct);

    // ── SYS - 사용자 메뉴 개인화 (설계서 20.12 즐겨찾기/최근 메뉴) ────────────

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

    // ── SYS - 사용자 등록 신청/승인 (설계서 19.3) ─────────────────────────────

    private sealed record UserIdAvailabilityPayload(bool Available);

    // 중복확인/신청은 로그인 전 화면이라 토큰 없이 직접 호출한다 (LoginAsync와 동일)
    public async Task<bool?> CheckUserIdAvailableAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"api/v1/users/exists/{Uri.EscapeDataString(userId)}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var payload = await resp.Content.ReadFromJsonAsync<UserIdAvailabilityPayload>(ct);
            return payload?.Available;
        }
        catch { return null; }
    }

    public async Task<(UserRequestDto? Request, string? Error)> RegisterUserAsync(
        object req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/users/request", req, ct);
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
        var url = "api/v1/users/requests" + (qs.Any() ? "?" + string.Join("&", qs) : "");
        return GetListAsync<UserRequestDto>(url, ct);
    }

    public Task<(UserRequestDto? Request, string? Error)> ApproveUserRequestAsync(
        string requestId, string? roleId, CancellationToken ct = default)
        => PatchWithErrorAsync<UserRequestDto>(
            $"api/v1/users/requests/{Uri.EscapeDataString(requestId)}/approve", new { roleId }, ct);

    public Task<(UserRequestDto? Request, string? Error)> RejectUserRequestAsync(
        string requestId, string reason, CancellationToken ct = default)
        => PatchWithErrorAsync<UserRequestDto>(
            $"api/v1/users/requests/{Uri.EscapeDataString(requestId)}/reject", new { reason }, ct);
}
