namespace NexaOne.Common;

public sealed record Error(string Code, string Description)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static readonly Error NullValue = new("Error.NullValue", "A null value was provided.");

    public static Error Validation(string code, string description) => new(code, description);
    public static Error Validation(string description) => new("Error.Validation", description);
    public static Error NotFound(string code, string description) => new(code, description);
    public static Error NotFound(string description) => new("Error.NotFound", description);
    public static Error Conflict(string code, string description) => new(code, description);
    public static Error Conflict(string description) => new("Error.Conflict", description);
    public static Error Failure(string code, string description) => new(code, description);
    public static Error Failure(string description) => new("Error.Failure", description);
}
