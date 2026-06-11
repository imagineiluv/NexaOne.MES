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
}
