using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Extensions;
using NexaOne.Common;

namespace NexaOne.UnitTests.Controllers;

/// <summary>
/// ControllerResultExtensions — 실패는 Error.Type으로 상태를 매핑하고(Validation/Failure→400·NotFound→404·
/// Conflict→409) 본문은 항상 Error 그대로({ Code, Description })를 유지하는지, 성공은 호출부가 고른
/// 형태(Ok(value)/onSuccess/NoContent/Ok())로 나가는지 검증한다.
/// </summary>
public sealed class ControllerResultExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Failure, StatusCodes.Status400BadRequest)]
    public void Generic_failure_maps_errortype_to_status_and_keeps_error_body(ErrorType type, int expected)
    {
        var error = ErrorOf(type);
        var result = Result.Failure<string>(error);

        var action = result.ToActionResult();

        // ToActionResult는 타입드 서브클래스(BadRequest/NotFound/Conflict ObjectResult)를 돌려준다 — 모두 ObjectResult 파생.
        var obj = action.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(expected);
        // 본문은 Error 그대로 — { Code, Description } 형태를 보존한다(ApiClient.ReadErrorAsync 계약).
        obj.Value.Should().BeSameAs(error);
    }

    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Failure, StatusCodes.Status400BadRequest)]
    public void NonGeneric_failure_maps_errortype_to_status_and_keeps_error_body(ErrorType type, int expected)
    {
        var error = ErrorOf(type);
        var result = Result.Failure(error);

        var action = result.ToActionResult();

        var obj = action.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(expected);
        obj.Value.Should().BeSameAs(error);
    }

    [Fact]
    public void Failure_body_carries_custom_code_from_two_arg_factory()
    {
        // 2-인자 팩터리의 Code는 필드명 등 임의 값 — 그래도 404로 매핑되고 Code/Description은 보존돼야 한다.
        var error = Error.NotFound("EquipmentAlarm", "ALM-404");
        var action = Result.Failure(error).ToActionResult();

        var obj = action.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var body = obj.Value.Should().BeOfType<Error>().Subject;
        body.Code.Should().Be("EquipmentAlarm");
        body.Description.Should().Be("ALM-404");
    }

    [Fact]
    public void Generic_success_defaults_to_ok_with_value()
    {
        var action = Result.Success(42).ToActionResult();

        var ok = action.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(42);
    }

    [Fact]
    public void Generic_success_uses_onSuccess_projection_when_provided()
    {
        var action = Result.Success(7).ToActionResult(v => new OkObjectResult($"n={v}"));

        var ok = action.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be("n=7");
    }

    [Fact]
    public void NonGeneric_success_defaults_to_no_content()
        => Result.Success().ToActionResult().Should().BeOfType<NoContentResult>();

    [Fact]
    public void NonGeneric_success_returns_ok_when_useNoContent_false()
        => Result.Success().ToActionResult(useNoContent: false).Should().BeOfType<OkResult>();

    private static Error ErrorOf(ErrorType type) => type switch
    {
        ErrorType.Validation => Error.Validation("field", "검증 실패"),
        ErrorType.NotFound => Error.NotFound("Entity", "없음"),
        ErrorType.Conflict => Error.Conflict("dup", "충돌"),
        _ => Error.Failure("일반 실패"),
    };
}
