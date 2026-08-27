namespace NexaOne.Web.Services.Api;

public interface IApiClient
{
    // 파일 기반 쿼리 레지스트리(저코드 경로) — query id로 등록된 쿼리를 실행해 동적 행 목록을 받는다.
    // 컴파일된 타입드 리포지토리(고코드, 속도·타입안전)와 공존하며, 기능별로 개발자가 선택해 쓴다.
    Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        string queryId, object? parameters = null, CancellationToken ct = default);

    // 제네릭 서버 페이징(read) — /query/{id}/paged로 {total, rows}를 받는다(DB-레벨 LIMIT).
    // null = 미지원/실패(404·422·구버전 서버) 신호 — 호출측(MetaScreen)이 전량 경로로 폴백한다.
    Task<PagedQueryResult?> ExecuteQueryPagedAsync(
        string queryId, object? parameters = null, int limit = 500, int offset = 0, CancellationToken ct = default);

    // MRP 실행(브리지 REST, pom:manage) — 실패(403/모듈 OFF 등)는 null(사유는 전역 토스트).
    // bucketDays/horizonBuckets 지정 시 기간 버킷(시간위상) 넷팅(v2 3단), 미지정=총량(v1).
    Task<MrpRunResultDto?> RunMrpAsync(int? bucketDays = null, int? horizonBuckets = null, CancellationToken ct = default);

    // MRP 실오더 전환 — 구매 제안은 구매오더, 생산 제안은 생산계획+생산관리오더로 전환한다.
    // 생산 제안마다 같은 공장의 활성 설비 배정이 필수다.
    Task<MrpConvertResultDto?> ConvertMrpAsync(
        string? runId = null,
        IReadOnlyList<string>? plannedOrderIds = null,
        IReadOnlyList<MrpProductionAssignmentDto>? productionAssignments = null,
        CancellationToken ct = default);

    // 등록된 쓰기(command) query id 실행 — 성공 여부 반환(저코드 폼 저장 경로). 감사 컬럼은 게이트웨이가 주입.
    Task<bool> ExecuteCommandAsync(
        string queryId, object? parameters = null, CancellationToken ct = default);

    // Auth
    /// <summary>유효한 액세스 토큰 반환 — 만료(임박) 시 갱신 후 공급한다. SignalR 재연결 협상용 (§20.9).</summary>
    Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default);
    Task<LoginResult> LoginAsync(string userId, string password, CancellationToken ct = default);
    Task LogoutAsync(string userId, string refreshToken, CancellationToken ct = default);
    Task ForgotPasswordAsync(string userId, string email, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword, CancellationToken ct = default);

    // MDM
    Task<List<EquipmentDto>> GetEquipmentListAsync(string plantId, CancellationToken ct = default);
    Task<EquipmentDto?> GetEquipmentAsync(string id, CancellationToken ct = default);
    Task<EquipmentDto?> CreateEquipmentAsync(object req, CancellationToken ct = default);
    Task DeleteEquipmentAsync(string id, CancellationToken ct = default);
    Task<List<PlantDto>> GetPlantsAsync(CancellationToken ct = default);
    Task<PlantDto?> CreatePlantAsync(object req, CancellationToken ct = default);
    Task<List<AreaDto>> GetAreasAsync(string plantId, CancellationToken ct = default);
    Task<AreaDto?> CreateAreaAsync(object req, CancellationToken ct = default);
    Task<List<ProductDto>> GetProductsAsync(CancellationToken ct = default);
    Task<ProductDto?> CreateProductAsync(object req, CancellationToken ct = default);
    Task<List<CodeClassDto>> GetCodeClassesAsync(CancellationToken ct = default);
    Task<CodeClassDto?> CreateCodeClassAsync(object req, CancellationToken ct = default);
    Task<List<CodeDto>> GetCodesAsync(string codeClassId, CancellationToken ct = default);
    Task<CodeDto?> CreateCodeAsync(object req, CancellationToken ct = default);

    // EPT - State Matrix
    Task<List<EquipmentStateMatrixDto>> GetStateMatrixAsync(string plantId, CancellationToken ct = default);
    Task<List<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(string plantId, string fromState, CancellationToken ct = default);
    Task<EquipmentStateMatrixDto?> UpsertStateMatrixAsync(object req, CancellationToken ct = default);
    /// <summary>설비 현재 상태 조회. 호출 실패 시 null (빈 결과와 구분된다 — §20.9 폴백 갱신용).</summary>
    Task<List<EquipmentCurrentStateDto>?> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default);
    Task<EquipmentCurrentStateDto?> ChangeEquipmentStateAsync(object req, CancellationToken ct = default);
    Task<List<EquipmentStateHistoryDto>> GetStateHistoryAsync(string equipmentId, CancellationToken ct = default);

    // EPT - Alarms
    /// <summary>알람 목록 조회. 호출 실패 시 null (빈 결과와 구분된다 — §20.9 폴백 갱신용).</summary>
    Task<List<AlarmDto>?> GetAlarmsAsync(string plantId, CancellationToken ct = default);
    Task ClearAlarmAsync(string alarmId, CancellationToken ct = default);

    // FDC
    Task<List<InterlockRuleDto>> GetInterlockRulesAsync(string equipmentId, CancellationToken ct = default);
    Task<InterlockRuleDto?> CreateInterlockRuleAsync(object req, CancellationToken ct = default);
    Task<List<FdcParameterDto>> GetFdcParametersAsync(string equipmentId, CancellationToken ct = default);
    Task<FdcParameterDto?> CreateFdcParameterAsync(object req, CancellationToken ct = default);
    Task<List<FdcCollectDataDto>> GetFdcCollectDataAsync(string parameterId, DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>최신 FDC 데이터 조회. 호출 실패 시 null (빈 결과와 구분된다).</summary>
    Task<List<FdcCollectDataDto>?> GetLatestFdcDataAsync(string parameterId, int limit = 50, CancellationToken ct = default);
    Task<FdcCollectDataDto?> RecordFdcDataAsync(object req, CancellationToken ct = default);
    Task<List<FdcInterlockHistoryDto>> GetInterlockHistoryAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<List<FdcParameterGroupDto>> GetFdcParameterGroupsAsync(string equipmentId, CancellationToken ct = default);
    Task<FdcParameterGroupDto?> CreateFdcParameterGroupAsync(object req, CancellationToken ct = default);
    Task<List<FdcAlarmConfigDto>> GetFdcAlarmConfigsAsync(string equipmentId, CancellationToken ct = default);
    Task<FdcAlarmConfigDto?> CreateFdcAlarmConfigAsync(object req, CancellationToken ct = default);
    Task<List<FdcAlarmHistoryDto>> GetFdcAlarmHistoryAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default);

    // Phase 4 후속 — Low-Code 화면 정의 저장소
    Task<List<ScreenDefinitionRecordDto>> GetScreenDefinitionsAsync(CancellationToken ct = default);
    /// <summary>대상 채널(MES|MOBILE|POP)로 화면 정의 목록을 필터한다. null/빈 값은 전체.</summary>
    Task<List<ScreenDefinitionRecordDto>> GetScreenDefinitionsAsync(
        string? targetChannel, CancellationToken ct = default);
    /// <summary>명명 쿼리 카탈로그(sys:manage, SQL 비노출) — S/O 관리(메타 카탈로그)·디자이너 공용.</summary>
    Task<List<QueryCatalogItemDto>> GetQueryCatalogAsync(CancellationToken ct = default);
    Task<ScreenDefinitionRecordDto?> GetScreenDefinitionAsync(string uiId, CancellationToken ct = default);
    Task SaveScreenDefinitionAsync(string uiId, string title, string definitionJson, CancellationToken ct = default);
    /// <summary>화면 정의와 진입 대상/완전 경로를 한 command로 원자 저장한다.</summary>
    Task SaveScreenDefinitionAsync(
        string uiId, string title, string definitionJson,
        string targetChannel, string? entryPath = null, CancellationToken ct = default);

    // RMS
    Task<List<RecipeDto>> GetRecipesAsync(string? equipmentClassId = null, string? state = null, CancellationToken ct = default);
    Task<RecipeDto?> CreateRecipeAsync(
        object req, string idempotencyKey, CancellationToken ct = default);
    Task RequestRecipeApprovalAsync(string recipeId, string idempotencyKey, CancellationToken ct = default);
    Task ApproveRecipe1Async(string recipeId, string idempotencyKey, CancellationToken ct = default);
    Task ApproveRecipe2Async(string recipeId, string idempotencyKey, CancellationToken ct = default);
    Task ReleaseRecipeAsync(string recipeId, string idempotencyKey, CancellationToken ct = default);
    Task RejectRecipeAsync(string recipeId, string reason, string idempotencyKey, CancellationToken ct = default);
    Task<RecipeDto?> CreateRecipeVersionAsync(
        string recipeId, string newRecipeId, string idempotencyKey,
        CancellationToken ct = default);
    Task<List<RecipeParamDto>> GetRecipeParamsAsync(string recipeId, CancellationToken ct = default);
    Task<RecipeParamDto?> AddRecipeParamAsync(
        string recipeId, object req, string idempotencyKey, CancellationToken ct = default);
    Task UpdateRecipeParamAsync(
        string paramId, string newValue, int expectedVersion, string idempotencyKey,
        CancellationToken ct = default);
    Task DeleteRecipeParamAsync(
        string paramId, int expectedVersion, string idempotencyKey,
        CancellationToken ct = default);

    // QMS
    Task<List<DefectDto>> GetDefectsAsync(string lotId, CancellationToken ct = default);
    Task<DefectDto?> RecordDefectAsync(object req, CancellationToken ct = default);
    Task ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default);
    Task<List<DefectClassDto>> GetDefectClassesAsync(CancellationToken ct = default);
    Task<DefectClassDto?> CreateDefectClassAsync(object req, CancellationToken ct = default);
    Task<List<InspectionSpecDto>> GetInspectionSpecsAsync(string? processId = null, CancellationToken ct = default);
    Task<InspectionSpecDto?> CreateInspectionSpecAsync(object req, CancellationToken ct = default);
    Task<List<InspectionResultDto>> GetInspectionResultsAsync(string lotId, CancellationToken ct = default);
    Task<InspectionResultDto?> RecordInspectionResultAsync(object req, CancellationToken ct = default);
    /// <summary>서버 생성 ID와 멱등키를 사용하는 다항목 검사 실행 v2를 확정합니다.</summary>
    Task<InspectionExecutionApiResult> RecordInspectionExecutionV2Async(
        RecordInspectionExecutionV2Request req, CancellationToken ct = default);
    Task<LotInspectionStatusDto?> GetLotInspectionStatusAsync(string lotId, CancellationToken ct = default);
    Task<List<SpcParamDto>> GetSpcParamsAsync(string equipmentId, CancellationToken ct = default);
    Task<SpcParamDto?> CreateSpcParamAsync(object req, CancellationToken ct = default);
    Task UpdateSpcLimitsAsync(string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default);
    Task<SpcLimitRevisionDto?> AddSpcLimitRevisionAsync(object req, CancellationToken ct = default);
    Task<SpcSubgroupEvaluationDto?> EvaluateSpcSubgroupAsync(object req, CancellationToken ct = default);
    Task<List<SpcRuleViolationDto>> GetSpcViolationsAsync(string? paramId = null, string? subgroupId = null, CancellationToken ct = default);
    Task<SamplingPlanRevisionDto?> AddSamplingPlanRevisionAsync(object req, CancellationToken ct = default);
    Task<SamplingPlanRevisionDto?> SelectSamplingPlanAsync(int lotSize, DateTime? effectiveAt = null, CancellationToken ct = default);
    Task<SamplingEvaluationDto?> EvaluateSamplingAsync(object req, CancellationToken ct = default);
    Task<AiModelVersionDto?> RegisterAiModelVersionAsync(object req, CancellationToken ct = default);
    Task<AiInferenceDto?> RecordAiInferenceAsync(object req, CancellationToken ct = default);
    Task<AiInferenceDto?> GetAiInferenceAsync(string inferenceId, CancellationToken ct = default);
    Task<List<AiReviewDto>> GetAiReviewsAsync(string inferenceId, CancellationToken ct = default);
    Task<AiReviewDto?> ReviewAiInferenceAsync(string inferenceId, object req, CancellationToken ct = default);

    // EMS
    Task<List<WorkOrderDto>> GetWorkOrdersAsync(string? equipmentId = null, string? status = null, CancellationToken ct = default);
    Task<WorkOrderDto?> CreateWorkOrderAsync(object req, CancellationToken ct = default);
    Task StartWorkOrderAsync(string woId, CancellationToken ct = default);
    Task CompleteWorkOrderAsync(string woId, string remark, CancellationToken ct = default);
    Task CancelWorkOrderAsync(string woId, CancellationToken ct = default);

    // POM 작업지시 관리/실행은 EMS 작업지시와 다른 모델이다. release/cancel을 포함한 상태전이 REST 경계를 통해
    // JWT 권한, 낙관적 버전, 멱등키, 채널/장치 감사 정보를 그대로 보존하고 HTTP 409도 호출자에게 돌려준다.
    Task<PomWorkOrderActionResult> CreatePomWorkOrderAsync(
        PomWorkOrderCreateRequest request,
        CancellationToken ct = default);
    Task<PomWorkOrderActionResult> ExecutePomWorkOrderActionAsync(
        string action,
        string workOrderId,
        PomWorkOrderActionRequest request,
        CancellationToken ct = default);

    // POM LOT 라우팅 실행 — 오류 상태/차단 사유를 작업자 화면까지 보존합니다.
    Task<PomRoutingApiResult<PomLotDto>> ExecutePomLotTrackInAsync(
        string lotId, PomLotTrackInRequest request, CancellationToken ct = default);
    Task<PomRoutingApiResult<PomLotDto>> ExecutePomLotTrackOutAsync(
        string lotId, PomLotTrackOutRequest request, CancellationToken ct = default);
    Task<PomRoutingApiResult<PomLotRoutingContextDto>> GetPomLotRoutingContextAsync(
        string lotId, CancellationToken ct = default);
    Task<PomRoutingApiResult<PomRoutingPolicyDecisionDto>> EvaluatePomLotRoutingAsync(
        string lotId, PomEvaluateRoutingRequest request, CancellationToken ct = default);
    Task<PomRoutingApiResult<PomLotDto>> ChangePomLotRoutingControlModeAsync(
        string lotId, PomChangeRoutingControlModeRequest request, CancellationToken ct = default);
    Task<PomRoutingApiResult<PomLotDto>> ApplyPomLotRouteDeviationAsync(
        string lotId, PomApplyRouteDeviationRequest request, CancellationToken ct = default);
    Task<PomRoutingApiResult<PomRouteExceptionDto>> RequestPomLotRouteExceptionAsync(
        string lotId, PomRequestRouteExceptionRequest request, CancellationToken ct = default);
    Task<PomRoutingApiResult<PomRouteExceptionDto>> ReviewPomLotRouteExceptionAsync(
        string action, string exceptionId, PomReviewRouteExceptionRequest request, CancellationToken ct = default);

    Task<List<MaintenancePlanDto>> GetMaintenancePlansAsync(string? equipmentId = null, CancellationToken ct = default);
    Task<MaintenancePlanDto?> CreateMaintenancePlanAsync(object req, CancellationToken ct = default);
    Task StartMaintenancePlanAsync(string planId, CancellationToken ct = default);
    Task CompleteMaintenancePlanAsync(string planId, CancellationToken ct = default);
    Task CancelMaintenancePlanAsync(string planId, CancellationToken ct = default);
    Task<List<SparePartDto>> GetSparePartsAsync(bool lowStock = false, CancellationToken ct = default);
    Task<SparePartDto?> CreateSparePartAsync(object req, CancellationToken ct = default);
    Task AdjustStockAsync(string partId, decimal delta, CancellationToken ct = default);

    // PPM
    Task<List<ProductionPlanDto>> GetPlansAsync(string plantId, CancellationToken ct = default);
    Task<ProductionPlanDto?> CreatePlanAsync(object req, CancellationToken ct = default);
    Task StartPlanAsync(string planId, CancellationToken ct = default);
    Task ReleasePlanAsync(string planId, CancellationToken ct = default);
    Task CompletePlanAsync(string planId, CancellationToken ct = default);
    Task CancelPlanAsync(string planId, CancellationToken ct = default);

    Task<List<ProductionOrderDto>> GetOrdersAsync(string planId, CancellationToken ct = default);
    Task<ProductionOrderDto?> CreateOrderAsync(object req, CancellationToken ct = default);
    Task StartOrderAsync(string orderId, CancellationToken ct = default);
    Task CompleteOrderAsync(string orderId, decimal actualQty, CancellationToken ct = default);
    Task CancelOrderAsync(string orderId, CancellationToken ct = default);

    // PPM - Lot TrackIn/TrackOut (설계서 19.4) — 조회(목록/경로/리포트)는 명명 쿼리(POM.*)가 단일 경로
    // 변경 호출은 (결과, 오류) 쌍을 반환한다 — 검증 실패 사유를 화면에 표시하기 위함
    Task<(LotDto? Lot, string? Error)> CreateLotAsync(object req, CancellationToken ct = default);
    Task<(LotDto? Lot, string? Error)> TrackInAsync(string lotId, object req, CancellationToken ct = default);
    Task<(LotDto? Lot, string? Error)> TrackOutAsync(string lotId, object req, CancellationToken ct = default);
    Task<(LotDto? Lot, string? Error)> MixingTrackInOutAsync(object req, CancellationToken ct = default);
    Task<bool> HoldLotAsync(string lotId, PomLotHoldRequest request, CancellationToken ct = default);
    Task<bool> ReleaseLotHoldAsync(string lotId, PomLotHoldRequest request, CancellationToken ct = default);

    // DLV — 조회(오더/품목/이력)는 명명 쿼리(SHP.*)가 단일 경로, 브리지는 생성/전이만
    Task<DeliveryOrderDto?> CreateDeliveryOrderAsync(object req, CancellationToken ct = default);
    Task ConfirmDeliveryOrderAsync(string orderId, CancellationToken ct = default);
    Task ShipDeliveryOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default);
    Task CancelDeliveryOrderAsync(string orderId, CancellationToken ct = default);

    // SYS — 조회(사용자/역할)는 명명 쿼리(SYS.*), 쓰기는 sys/admin 브리지.
    Task DeactivateUserAsync(string userId, CancellationToken ct = default);
    /// <summary>§20.10 관리자 잠금 해제 — 잠금은 보안 상태라 인증 경로(auth 라우트)가 소유한다(S7).</summary>
    Task UnlockUserAsync(string userId, CancellationToken ct = default);
    Task<RoleDto?> CreateRoleAsync(object req, CancellationToken ct = default);
    Task AddPermissionAsync(string roleId, string permission, CancellationToken ct = default);
    Task RemovePermissionAsync(string roleId, string permission, CancellationToken ct = default);

    // SYS - 사용자 메뉴 개인화 (설계서 20.12 즐겨찾기/최근 메뉴) — 토큰 사용자 스코프(자기 데이터만)
    // 쓰기는 성공 여부를 반환한다 — 실패 시 캐시를 갱신하지 않기 위함 (§20.8과 동일 원칙)
    Task<VirtualEventEvaluationDto?> EvaluateVirtualEventAsync(string equipmentId, string eventId, CancellationToken ct = default);
    Task<string> GetUserLanguageAsync(CancellationToken ct = default);
    Task<bool> SetUserLanguageAsync(string language, CancellationToken ct = default);
    Task<Dictionary<string, string>> GetLanguageResourcesAsync(string language, CancellationToken ct = default);
    Task<List<Dictionary<string, object?>>> GetMenuTreeAsync(CancellationToken ct = default);
    Task<List<FavoriteMenuDto>> GetFavoriteMenusAsync(CancellationToken ct = default);
    Task<bool> AddFavoriteMenuAsync(string menuId, CancellationToken ct = default);
    Task<bool> RemoveFavoriteMenuAsync(string menuId, CancellationToken ct = default);
    Task<bool> ReorderFavoriteMenusAsync(List<string> menuIds, CancellationToken ct = default);
    Task<List<RecentMenuDto>> GetRecentMenusAsync(CancellationToken ct = default);
    Task<bool> RecordRecentMenuAsync(string menuId, CancellationToken ct = default);

    // SYS - Deploy (설계서 20.11 배포 파일 업로드/클라이언트 업데이트) — 관리=sys:manage, latest=인증만
    Task<List<DeployFileDto>> GetDeployFilesAsync(CancellationToken ct = default);
    Task<DeployFileDto?> GetLatestDeployAsync(CancellationToken ct = default);
    /// <summary>배포 파일 업로드(multipart). 실패 시 서버 오류 메시지를 보존해 화면에 표시한다.</summary>
    Task<(DeployFileDto? File, string? Error)> UploadDeployFileAsync(
        Stream content, string fileName, string version, string description, bool forceUpdate,
        CancellationToken ct = default);
    Task<bool> SetDeployFileActiveAsync(string fileId, bool isActive, CancellationToken ct = default);

    // SYS - ConditionSetting (설계서 20.8 조건 저장/불러오기) — 토큰 사용자 스코프
    // 쓰기/삭제는 성공 여부를 반환한다 — 실패를 UI에서 구분해 낙관적 상태 갱신을 막기 위함
    Task<ConditionSettingDto?> GetConditionSettingsAsync(string menuId, CancellationToken ct = default);
    Task<ConditionItemDto?> SaveConditionAsync(string menuId, string name, Dictionary<string, string?> values, CancellationToken ct = default);
    Task<bool> SaveLatestConditionAsync(string menuId, Dictionary<string, string?> values, CancellationToken ct = default);
    Task<bool> DeleteConditionAsync(string menuId, string name, CancellationToken ct = default);
    Task<bool> ClearLatestConditionAsync(string menuId, CancellationToken ct = default);

    // SYS - 사용자 등록 신청/승인 (설계서 19.3)
    // 신청/중복확인은 익명 호출(로그인 전 화면), 목록/승인/반려는 ADMIN 전용
    /// <summary>아이디 사용 가능 여부. 호출 실패 시 null (사용 불가와 구분된다).</summary>
    Task<bool?> CheckUserIdAvailableAsync(string userId, CancellationToken ct = default);
    Task<(UserRequestDto? Request, string? Error)> RegisterUserAsync(object req, CancellationToken ct = default);
    Task<List<UserRequestDto>> GetUserRequestsAsync(
        string? plantId = null, string? status = null, string? userId = null,
        string? userName = null, string? email = null,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
    /// <summary>승인 — 응답에 1회 표시용 임시 비밀번호가 포함된다(관리자 전달용, 최초 로그인 시 변경 강제).</summary>
    Task<(UserRequestApprovalDto? Approval, string? Error)> ApproveUserRequestAsync(
        string requestId, string? roleId, CancellationToken ct = default);
    Task<(UserRequestDto? Request, string? Error)> RejectUserRequestAsync(
        string requestId, string reason, CancellationToken ct = default);
}
