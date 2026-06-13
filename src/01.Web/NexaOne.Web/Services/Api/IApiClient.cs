namespace NexaOne.Web.Services.Api;

public interface IApiClient
{
    // Dashboard
    Task<DashboardSummaryDto?> GetDashboardAsync(CancellationToken ct = default);

    // Auth
    /// <summary>유효한 액세스 토큰 반환 — 만료(임박) 시 갱신 후 공급한다. SignalR 재연결 협상용 (§20.9).</summary>
    Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default);
    Task<LoginResult> LoginAsync(string userId, string password, CancellationToken ct = default);
    Task LogoutAsync(string userId, string refreshToken, CancellationToken ct = default);
    Task ForgotPasswordAsync(string userId, string email, CancellationToken ct = default);
    Task ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword, CancellationToken ct = default);

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
    Task<ScreenDefinitionRecordDto?> GetScreenDefinitionAsync(string uiId, CancellationToken ct = default);
    Task SaveScreenDefinitionAsync(string uiId, string title, string definitionJson, CancellationToken ct = default);

    // RMS
    Task<List<RecipeDto>> GetRecipesAsync(string? equipmentClassId = null, string? state = null, CancellationToken ct = default);
    Task<RecipeDto?> CreateRecipeAsync(object req, CancellationToken ct = default);
    Task RequestRecipeApprovalAsync(string recipeId, CancellationToken ct = default);
    Task ApproveRecipe1Async(string recipeId, string approverId, CancellationToken ct = default);
    Task ApproveRecipe2Async(string recipeId, string approverId, CancellationToken ct = default);
    Task ReleaseRecipeAsync(string recipeId, string approverId, CancellationToken ct = default);
    Task RejectRecipeAsync(string recipeId, string reason, CancellationToken ct = default);
    Task<RecipeDto?> CreateRecipeVersionAsync(string recipeId, string newRecipeId, CancellationToken ct = default);
    Task<List<RecipeParamDto>> GetRecipeParamsAsync(string recipeId, CancellationToken ct = default);
    Task<RecipeParamDto?> AddRecipeParamAsync(string recipeId, object req, CancellationToken ct = default);
    Task UpdateRecipeParamAsync(string paramId, string newValue, CancellationToken ct = default);
    Task DeleteRecipeParamAsync(string paramId, CancellationToken ct = default);

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
    Task<List<SpcParamDto>> GetSpcParamsAsync(string equipmentId, CancellationToken ct = default);
    Task<SpcParamDto?> CreateSpcParamAsync(object req, CancellationToken ct = default);
    Task UpdateSpcLimitsAsync(string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default);

    // EMS
    Task<List<WorkOrderDto>> GetWorkOrdersAsync(string? equipmentId = null, string? status = null, CancellationToken ct = default);
    Task<WorkOrderDto?> CreateWorkOrderAsync(object req, CancellationToken ct = default);
    Task StartWorkOrderAsync(string woId, CancellationToken ct = default);
    Task CompleteWorkOrderAsync(string woId, string remark, CancellationToken ct = default);
    Task CancelWorkOrderAsync(string woId, CancellationToken ct = default);
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

    // PPM - Lot TrackIn/TrackOut (설계서 19.4)
    // 변경 호출은 (결과, 오류) 쌍을 반환한다 — 검증 실패 사유를 화면에 표시하기 위함
    Task<List<LotDto>> GetLotsAsync(string plantId, string? state = null, CancellationToken ct = default);
    Task<LotRouteDto?> GetLotRouteAsync(string lotId, CancellationToken ct = default);
    Task<(LotDto? Lot, string? Error)> CreateLotAsync(object req, CancellationToken ct = default);
    Task<(LotDto? Lot, string? Error)> TrackInAsync(string lotId, object req, CancellationToken ct = default);
    Task<(LotDto? Lot, string? Error)> TrackOutAsync(string lotId, object req, CancellationToken ct = default);
    Task<(LotDto? Lot, string? Error)> MixingTrackInOutAsync(object req, CancellationToken ct = default);
    Task<bool> HoldLotAsync(string lotId, CancellationToken ct = default);
    Task<bool> ReleaseLotHoldAsync(string lotId, CancellationToken ct = default);
    Task<List<LotHistoryDto>> GetLotTrackingReportAsync(
        string plantId, string? lotId = null, string? equipmentId = null, string? processId = null,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default);

    // DLV
    Task<List<DeliveryOrderDto>> GetDeliveryOrdersAsync(string plantId, CancellationToken ct = default);
    Task<DeliveryOrderDto?> CreateDeliveryOrderAsync(object req, CancellationToken ct = default);
    Task ConfirmDeliveryOrderAsync(string orderId, CancellationToken ct = default);
    Task ShipDeliveryOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default);
    Task CancelDeliveryOrderAsync(string orderId, CancellationToken ct = default);
    Task<List<DeliveryItemDto>> GetDeliveryItemsAsync(string orderId, CancellationToken ct = default);
    Task<DeliveryItemDto?> AddDeliveryItemAsync(string orderId, object req, CancellationToken ct = default);
    Task SetDeliveryItemActualQtyAsync(string itemId, decimal actualQty, CancellationToken ct = default);
    Task<List<ShipmentHistoryDto>> GetShipmentHistoryAsync(string orderId, CancellationToken ct = default);
    Task<ShipmentHistoryDto?> RecordShipmentAsync(string orderId, object req, CancellationToken ct = default);

    // SYS
    Task<List<UserDto>> GetUsersAsync(CancellationToken ct = default);
    Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default);
    Task<UserDto?> CreateUserAsync(object req, CancellationToken ct = default);
    Task DeactivateUserAsync(string userId, CancellationToken ct = default);
    Task UnlockUserAsync(string userId, CancellationToken ct = default);
    Task<List<RoleDto>> GetRolesAsync(CancellationToken ct = default);
    Task<RoleDto?> GetRoleAsync(string roleId, CancellationToken ct = default);
    Task<RoleDto?> CreateRoleAsync(object req, CancellationToken ct = default);
    Task AddPermissionAsync(string roleId, string permission, CancellationToken ct = default);
    Task RemovePermissionAsync(string roleId, string permission, CancellationToken ct = default);
    Task<List<MultiLanguageResourceDto>> GetLanguageResourcesAsync(string? menuId = null, string? language = null, CancellationToken ct = default);
    Task<MultiLanguageResourceDto?> UpsertLanguageResourceAsync(object req, CancellationToken ct = default);

    // SYS - Menu
    Task<List<MenuItemDto>> GetMenuAsync(CancellationToken ct = default);

    // SYS - ConditionSetting (설계서 20.8 조건 저장/불러오기)
    // 쓰기/삭제는 성공 여부를 반환한다 — 실패를 UI에서 구분해 낙관적 상태 갱신을 막기 위함
    Task<ConditionSettingDto?> GetConditionSettingsAsync(string menuId, CancellationToken ct = default);
    Task<ConditionItemDto?> SaveConditionAsync(string menuId, string name, Dictionary<string, string?> values, CancellationToken ct = default);
    Task<bool> SaveLatestConditionAsync(string menuId, Dictionary<string, string?> values, CancellationToken ct = default);
    Task<bool> DeleteConditionAsync(string menuId, string name, CancellationToken ct = default);
    Task<bool> ClearLatestConditionAsync(string menuId, CancellationToken ct = default);

    // SYS - Deploy (설계서 20.11 배포 파일 업로드/클라이언트 업데이트, ADMIN 전용)
    Task<List<DeployFileDto>> GetDeployFilesAsync(CancellationToken ct = default);
    Task<DeployLatestDto?> GetLatestDeployAsync(CancellationToken ct = default);
    /// <summary>배포 파일 업로드(multipart). 실패 시 서버 오류 메시지를 보존해 화면에 표시한다.</summary>
    Task<(DeployFileDto? File, string? Error)> UploadDeployFileAsync(
        Stream content, string fileName, string version, string description, bool forceUpdate,
        CancellationToken ct = default);
    Task<bool> SetDeployFileActiveAsync(string fileId, bool isActive, CancellationToken ct = default);

    // SYS - 사용자 메뉴 개인화 (설계서 20.12 즐겨찾기/최근 메뉴)
    // 쓰기는 성공 여부를 반환한다 — 실패 시 캐시를 갱신하지 않기 위함 (§20.8과 동일 원칙)
    Task<List<FavoriteMenuDto>> GetFavoriteMenusAsync(CancellationToken ct = default);
    Task<bool> AddFavoriteMenuAsync(string menuId, CancellationToken ct = default);
    Task<bool> RemoveFavoriteMenuAsync(string menuId, CancellationToken ct = default);
    Task<bool> ReorderFavoriteMenusAsync(List<string> menuIds, CancellationToken ct = default);
    Task<List<RecentMenuDto>> GetRecentMenusAsync(CancellationToken ct = default);
    Task<bool> RecordRecentMenuAsync(string menuId, CancellationToken ct = default);

    // SYS - 사용자 등록 신청/승인 (설계서 19.3)
    // 신청/중복확인은 익명 호출(로그인 전 화면), 목록/승인/반려는 ADMIN 전용
    /// <summary>아이디 사용 가능 여부. 호출 실패 시 null (사용 불가와 구분된다).</summary>
    Task<bool?> CheckUserIdAvailableAsync(string userId, CancellationToken ct = default);
    Task<(UserRequestDto? Request, string? Error)> RegisterUserAsync(object req, CancellationToken ct = default);
    Task<List<UserRequestDto>> GetUserRequestsAsync(
        string? plantId = null, string? status = null, string? userId = null,
        string? userName = null, string? email = null,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
    Task<(UserRequestDto? Request, string? Error)> ApproveUserRequestAsync(
        string requestId, string? roleId, CancellationToken ct = default);
    Task<(UserRequestDto? Request, string? Error)> RejectUserRequestAsync(
        string requestId, string reason, CancellationToken ct = default);
}
