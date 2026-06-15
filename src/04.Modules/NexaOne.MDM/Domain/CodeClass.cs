using NexaOne.Common;

namespace NexaOne.MDM.Domain;

public sealed class CodeClass : AuditableEntity<string>
{
    private CodeClass(string codeClassId) : base(codeClassId) { }

    public string CodeClassName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;

    public static Result<CodeClass> Create(string codeClassId, string codeClassName)
    {
        if (string.IsNullOrWhiteSpace(codeClassId))
            return Result.Failure<CodeClass>(Error.Validation(nameof(codeClassId), "Code class ID is required."));
        if (string.IsNullOrWhiteSpace(codeClassName))
            return Result.Failure<CodeClass>(Error.Validation(nameof(codeClassName), "Code class name is required."));

        var codeClass = new CodeClass(codeClassId)
        {
            CodeClassName = codeClassName
        };
        return codeClass;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 Description을 받지 않아, 저장된 설명이 읽기경로에서 매번 빈 문자열로 유실되는
    /// 상태손실을 막는다.</summary>
    public static CodeClass Restore(string codeClassId, string codeClassName, string description)
        => new(codeClassId)
        {
            CodeClassName = codeClassName,
            Description = description
        };
}
