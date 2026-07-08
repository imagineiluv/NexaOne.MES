using NexaOne.Common;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Conditions;

/// <summary>
/// 조건 저장/불러오기 (설계서 20.8).
/// - 사용자 저장 조건은 메뉴당 최대 <see cref="MaxSavedConditions"/>개. 초과 시 SavedAt이 가장 오래된 것부터 자동 삭제.
/// - '$latest'(마지막 조회 조건)는 한도에 포함하지 않고 수동 삭제 대상에서도 제외한다.
/// </summary>
public sealed class ConditionSettingService
{
    // 단일 출처 = NexaOne.ServiceContracts.Sys.PersonalizationRules (호스트 게이트웨이와 공유 — 미러 금지).
    public const int DefaultMaxSavedConditions = NexaOne.ServiceContracts.Sys.PersonalizationRules.MaxSavedConditions;

    /// <summary>MENU_ID 컬럼(NVARCHAR(100))에 맞춘 menuId 최대 길이.</summary>
    public const int MaxMenuIdLength = NexaOne.ServiceContracts.Sys.PersonalizationRules.MaxMenuIdLength;

    /// <summary>VALUES_JSON 저장 상한(문자 수). NVARCHAR(MAX) 무제한 누적을 막는다.</summary>
    public const int MaxValuesJsonLength = NexaOne.ServiceContracts.Sys.PersonalizationRules.MaxValuesJsonLength;

    private readonly IConditionSettingRepository _repository;

    public ConditionSettingService(IConditionSettingRepository repository, int maxSavedConditions = DefaultMaxSavedConditions)
    {
        _repository = repository;
        MaxSavedConditions = maxSavedConditions > 0 ? maxSavedConditions : DefaultMaxSavedConditions;
    }

    public int MaxSavedConditions { get; }

    public async Task<Result<IReadOnlyList<ConditionSetting>>> GetConditionsAsync(
        string userId, string menuId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure<IReadOnlyList<ConditionSetting>>(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(menuId))
            return Result.Failure<IReadOnlyList<ConditionSetting>>(Error.Validation("MenuId is required."));

        var settings = await _repository.GetByMenuAsync(userId, NormalizeMenuId(menuId), ct);
        return Result.Success(settings);
    }

    public async Task<Result<ConditionSetting>> SaveConditionAsync(
        string userId, string menuId, string name, string valuesJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure<ConditionSetting>(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(menuId))
            return Result.Failure<ConditionSetting>(Error.Validation("MenuId is required."));
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<ConditionSetting>(Error.Validation("조건명을 입력해 주세요."));

        name = name.Trim();
        // DB 콜레이션(대소문자 무시) 우회를 막기 위해 '$' 접두 조건명 전체를 예약한다 ($Latest, $LATEST 포함)
        if (name.StartsWith("$", StringComparison.Ordinal))
            return Result.Failure<ConditionSetting>(Error.Validation("'$'로 시작하는 조건명은 예약되어 사용할 수 없습니다."));
        if (name.Length > 100)
            return Result.Failure<ConditionSetting>(Error.Validation("조건명은 100자 이하여야 합니다."));

        var normalizedMenuId = NormalizeMenuId(menuId);
        if (normalizedMenuId.Length > MaxMenuIdLength)
            return Result.Failure<ConditionSetting>(Error.Validation($"메뉴 ID는 {MaxMenuIdLength}자 이하여야 합니다."));
        if (valuesJson is { Length: > MaxValuesJsonLength })
            return Result.Failure<ConditionSetting>(Error.Validation($"조건 값이 너무 큽니다 (최대 {MaxValuesJsonLength:N0}자)."));

        var setting = ConditionSetting.Create(userId, normalizedMenuId, name, valuesJson ?? "{}", DateTime.UtcNow);
        await _repository.UpsertAsync(setting, ct);

        // 한도 초과 시 savedAt이 가장 오래된 사용자 저장 조건부터 자동 삭제 ($latest 제외)
        var all = await _repository.GetByMenuAsync(userId, normalizedMenuId, ct);
        var userSaved = all.Where(s => !s.IsLatest).OrderBy(s => s.SavedAt).ToList();
        foreach (var stale in userSaved.Take(Math.Max(0, userSaved.Count - MaxSavedConditions)))
            await _repository.DeleteAsync(userId, normalizedMenuId, stale.Name, ct);

        return Result.Success(setting);
    }

    public async Task<Result<ConditionSetting>> SaveLatestAsync(
        string userId, string menuId, string valuesJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure<ConditionSetting>(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(menuId))
            return Result.Failure<ConditionSetting>(Error.Validation("MenuId is required."));

        var normalizedMenuId = NormalizeMenuId(menuId);
        if (normalizedMenuId.Length > MaxMenuIdLength)
            return Result.Failure<ConditionSetting>(Error.Validation($"메뉴 ID는 {MaxMenuIdLength}자 이하여야 합니다."));
        if (valuesJson is { Length: > MaxValuesJsonLength })
            return Result.Failure<ConditionSetting>(Error.Validation($"조건 값이 너무 큽니다 (최대 {MaxValuesJsonLength:N0}자)."));

        var setting = ConditionSetting.Create(
            userId, normalizedMenuId, ConditionSetting.LatestName, valuesJson ?? "{}", DateTime.UtcNow);
        await _repository.UpsertAsync(setting, ct);
        return Result.Success(setting);
    }

    public async Task<Result> DeleteConditionAsync(
        string userId, string menuId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(menuId))
            return Result.Failure(Error.Validation("MenuId is required."));
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("조건명을 입력해 주세요."));

        // $latest는 수동 삭제 대상에서 제외 — '최근 조건 초기화'(ClearLatest)로만 비운다 (설계 20.8)
        // DB 콜레이션이 대소문자/후행 공백을 무시하므로 Trim + OrdinalIgnoreCase로 우회를 차단한다
        name = name.Trim();
        if (string.Equals(name, ConditionSetting.LatestName, StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Validation("최근 조건은 '최근 조건 초기화'로만 삭제할 수 있습니다."));

        var normalizedMenuId = NormalizeMenuId(menuId);
        var existing = await _repository.GetAsync(userId, normalizedMenuId, name, ct);
        if (existing is null)
            return Result.Failure(Error.NotFound($"저장된 조건 '{name}'을 찾을 수 없습니다."));

        await _repository.DeleteAsync(userId, normalizedMenuId, name, ct);
        return Result.Success();
    }

    public async Task<Result> ClearLatestAsync(string userId, string menuId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(menuId))
            return Result.Failure(Error.Validation("MenuId is required."));

        await _repository.DeleteAsync(userId, NormalizeMenuId(menuId), ConditionSetting.LatestName, ct);
        return Result.Success();
    }

    // 라우트 경로 기반 menuId는 대소문자/후행 슬래시 변형('/fdc/monitor/' vs '/fdc/monitor')이
    // 서로 다른 버킷으로 갈라지지 않도록 정규화한다. 루트 경로("/")는 그대로 둔다.
    private static string NormalizeMenuId(string menuId)
    {
        var normalized = menuId.Trim().TrimEnd('/').ToLowerInvariant();
        return normalized.Length == 0 ? "/" : normalized;
    }
}
