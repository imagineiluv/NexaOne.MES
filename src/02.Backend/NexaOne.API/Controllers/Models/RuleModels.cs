namespace NexaOne.API.Controllers.Models;

public class RuleRequest
{
    public RuleHead Head { get; set; } = new();
    public Dictionary<string, object>? Body { get; set; }
}

public class RuleHead
{
    public string? RuleName { get; set; }
    public string? UserId { get; set; }
    public string? PlantId { get; set; }
    public string? LanguageType { get; set; } = "ko-KR";
    public int Timeout { get; set; } = 30;
}

public class RuleResponse<T>
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public T? Data { get; set; }
}

public class QueryRequest
{
    public RuleHead Head { get; set; } = new();
    public string Sql { get; set; } = string.Empty;
    public Dictionary<string, object>? Parameters { get; set; }
}

public class ProcedureRequest
{
    public RuleHead Head { get; set; } = new();
    public string ProcedureName { get; set; } = string.Empty;
    public Dictionary<string, object>? Parameters { get; set; }
}
