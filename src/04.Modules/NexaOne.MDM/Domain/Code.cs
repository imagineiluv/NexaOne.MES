using NexaOne.Common;

namespace NexaOne.MDM.Domain;

public sealed class Code : AuditableEntity<string>
{
    private Code(string codeId) : base(codeId) { }

    public string CodeClassId { get; private set; } = string.Empty;
    public string CodeName { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    public string ValidState { get; private set; } = "Valid";

    public static Result<Code> Create(
        string codeId,
        string codeClassId,
        string codeName,
        int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(codeId))
            return Result.Failure<Code>(Error.Validation(nameof(codeId), "Code ID is required."));
        if (string.IsNullOrWhiteSpace(codeName))
            return Result.Failure<Code>(Error.Validation(nameof(codeName), "Code name is required."));

        var code = new Code(codeId)
        {
            CodeClassId = codeClassId,
            CodeName = codeName,
            SortOrder = sortOrder,
            ValidState = "Valid"
        };
        return code;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 ValidState를 항상 "Valid"로 고정해, 무효화된 코드(예: "Invalid")가 읽기경로에서
    /// 유효한 것으로 되살아나는 상태손실을 막는다(GetByClass는 SQL 필터로 가려졌으나 손실 자체는 실재).</summary>
    public static Code Restore(
        string codeId, string codeClassId, string codeName, int sortOrder, string validState,
        string? createdBy = null, DateTime? createdAt = null,
        string? updatedBy = null, DateTime? updatedAt = null)
    {
        var code = new Code(codeId)
        {
            CodeClassId = codeClassId,
            CodeName = codeName,
            SortOrder = sortOrder,
            ValidState = validState
        };
        code.RestoreAudit(createdBy ?? code.CreatedBy, createdAt ?? code.CreatedAt, updatedBy, updatedAt);
        return code;
    }
}
