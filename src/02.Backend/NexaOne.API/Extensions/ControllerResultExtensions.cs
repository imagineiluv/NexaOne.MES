using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;

namespace NexaOne.API.Extensions;

/// <summary>
/// Result/Result&lt;T&gt; → IActionResult 변환을 한 곳으로 모은다. 실패는 Error.Type으로 상태를 매핑하고
/// (Validation→400·NotFound→404·Conflict→409·Failure→400) 본문은 항상 Error 그대로({ Code, Description })를
/// 유지한다 — 웹 ApiClient.ReadErrorAsync가 그 형태를 읽으므로 ProblemDetails로 바꾸지 않는다.
/// 성공 형태(Ok(value)/Ok()/NoContent())는 호출부가 선택한다.
/// </summary>
public static class ControllerResultExtensions
{
    // 본문을 Error 그대로 둬서 { Code, Description } 형태를 보존한다(ApiClient가 기대하는 형태).
    // 의미에 맞는 타입드 결과를 돌려준다(BadRequest/NotFound/Conflict) — 상태코드와 .Value(Error)가 모두 일관된다.
    private static ObjectResult Problem(Error error) => error.Type switch
    {
        ErrorType.NotFound => new NotFoundObjectResult(error),
        ErrorType.Conflict => new ConflictObjectResult(error),
        _ => new BadRequestObjectResult(error),   // Validation·Failure(및 미분류)는 400
    };

    /// <summary>Result&lt;T&gt; 변환. 성공 시 onSuccess(value) ?? Ok(value).</summary>
    public static IActionResult ToActionResult<T>(this Result<T> result, Func<T, IActionResult>? onSuccess = null)
        => result.IsSuccess
            ? onSuccess?.Invoke(result.Value) ?? new OkObjectResult(result.Value)
            : Problem(result.Error);

    /// <summary>본문 없는 Result 변환. 기본 성공은 204 No Content이며,
    /// useNoContent:false면 본문 없는 200 OK를 돌려준다(기존 Ok() 호출부 보존).</summary>
    public static IActionResult ToActionResult(this Result result, bool useNoContent = true)
        => result.IsSuccess
            ? (useNoContent ? new NoContentResult() : new OkResult())
            : Problem(result.Error);
}
