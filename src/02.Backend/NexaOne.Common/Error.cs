namespace NexaOne.Common;

/// <summary>오류의 의미 범주 — HTTP 상태 매핑의 단일 근거. Code는 2-인자 팩터리에서
/// 필드명 등 임의 값이 되므로(예: Validation(nameof(field), …)) 상태 판정에 쓰지 않는다.</summary>
public enum ErrorType
{
    Failure,
    Validation,
    NotFound,
    Conflict,
}

public sealed record Error(string Code, string Description, ErrorType Type = ErrorType.Failure)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static readonly Error NullValue = new("Error.NullValue", "A null value was provided.");

    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);
    public static Error Validation(string description) => new("Error.Validation", description, ErrorType.Validation);
    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);
    public static Error NotFound(string description) => new("Error.NotFound", description, ErrorType.NotFound);
    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);
    public static Error Conflict(string description) => new("Error.Conflict", description, ErrorType.Conflict);
    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);
    public static Error Failure(string description) => new("Error.Failure", description, ErrorType.Failure);
}
