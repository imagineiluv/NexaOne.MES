using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>Result/Result&lt;T&gt; → IActionResult — NexaOne.API ControllerResultExtensions와 동일 매핑
/// (Conflict→409, NotFound→404, Validation/Failure→400; 성공→Ok(value)/NoContent). 호스트가 API를 참조하지 않으므로 로컬 정의.</summary>
public static class BridgeResultExtensions
{
    private static ObjectResult Problem(Error error) => error.Type switch
    {
        ErrorType.NotFound => new NotFoundObjectResult(error),
        ErrorType.Conflict => new ConflictObjectResult(error),
        _ => new BadRequestObjectResult(error),   // Validation·Failure(및 미분류)는 400
    };

    public static IActionResult ToActionResult<T>(this Result<T> result, Func<T, IActionResult>? onSuccess = null)
        => result.IsSuccess
            ? onSuccess?.Invoke(result.Value) ?? new OkObjectResult(result.Value)
            : Problem(result.Error);

    public static IActionResult ToActionResult(this Result result, bool useNoContent = true)
        => result.IsSuccess
            ? (useNoContent ? new NoContentResult() : new OkResult())
            : Problem(result.Error);
}
