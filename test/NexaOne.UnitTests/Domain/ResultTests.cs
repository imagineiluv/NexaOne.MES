using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class ResultTests
{
    [Fact]
    public void Success_result_has_no_error()
    {
        var result = Result.Success();
        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Error.Should().Be(Error.None);
    }

    [Fact]
    public void Failure_result_carries_error()
    {
        var error = Error.NotFound("User", "u001");
        var result = Result.Failure(error);
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Success_with_value_returns_value()
    {
        var result = Result.Success(42);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_with_value_throws_on_access()
    {
        var result = Result.Failure<int>(Error.Validation("x", "bad"));
        result.IsFailure.Should().BeTrue();
        var act = () => _ = result.Value;
        act.Should().Throw<InvalidOperationException>();
    }
}
