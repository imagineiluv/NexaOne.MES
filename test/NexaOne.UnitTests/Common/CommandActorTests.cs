using FluentAssertions;
using NexaOne.Application.Auditing;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.UnitTests.Common;

public sealed class CommandActorTests
{
    [Fact]
    public void Resolve_prefers_explicit_actor_and_trims_it()
    {
        WithCurrentUser("ambient-user", () =>
        {
            var result = CommandActor.Resolve(" explicit-user ");

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be("explicit-user");
        });
    }

    [Fact]
    public void Resolve_uses_authenticated_ambient_actor()
    {
        WithCurrentUser("ambient-user", () =>
            CommandActor.Resolve(null).Value.Should().Be("ambient-user"));
    }

    [Fact]
    public void Resolve_fails_closed_without_an_actor()
    {
        WithCurrentUser(null, () =>
        {
            var result = CommandActor.Resolve(null);

            result.IsFailure.Should().BeTrue();
            result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        });
    }

    private static void WithCurrentUser(string? userId, Action assertion)
    {
        var previous = CurrentUserContext.UserId;
        try
        {
            CurrentUserContext.UserId = userId;
            assertion();
        }
        finally
        {
            CurrentUserContext.UserId = previous;
        }
    }
}
