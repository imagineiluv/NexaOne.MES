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
}
