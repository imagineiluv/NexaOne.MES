using System.Text.Json.Serialization;

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
    /// <summary>다국어 리소스 키(P3-14 서버 오류 다국어). 지정 시 응답 경계 필터가 요청 언어로 Description을
    /// 치환한다(미지정=Description 한국어 그대로). 서버 내부 전용이라 응답 JSON에 직렬화하지 않는다.</summary>
    [JsonIgnore] public string? MessageKey { get; init; }

    /// <summary>리소스 템플릿의 string.Format 인자({0},{1}…). 응답 경계 필터가 번역 시 사용한다.</summary>
    [JsonIgnore] public IReadOnlyList<string>? MessageArgs { get; init; }

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

    /// <summary>표준 "찾을 수 없습니다" 오류 + 다국어 키(P3-14). 엔티티명·식별자를 인자로 받아 한국어
    /// Description(폴백)을 만들고, MessageKey="error.notFound"·MessageArgs=[entity,id]를 실어 응답 경계가
    /// 요청 언어로 번역할 수 있게 한다. Code는 상태 판정 무관이라 관례대로 엔티티명(nameof)을 쓴다.</summary>
    public static Error NotFoundOf(string entity, string id) =>
        new(entity, $"{entity} '{id}'을(를) 찾을 수 없습니다.", ErrorType.NotFound)
        {
            MessageKey = "error.notFound",
            MessageArgs = new[] { entity, id },
        };
}
