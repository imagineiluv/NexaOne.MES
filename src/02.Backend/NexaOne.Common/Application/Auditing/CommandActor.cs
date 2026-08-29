using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.Application.Auditing;

/// <summary>
/// Resolves the authenticated command actor. Background work must pass an explicit service actor
/// (for example, <c>SYSTEM</c>) instead of silently manufacturing one at the write boundary.
/// </summary>
public static class CommandActor
{
    public static Result<string> Resolve(
        string? actorId,
        string parameterName = "ActorId",
        int maximumLength = 50)
    {
        var resolved = Clean(actorId) ?? Clean(CurrentUserContext.UserId);
        if (resolved is null)
        {
            return Result.Failure<string>(Error.Validation(
                parameterName,
                "An authenticated actor is required. Background work must provide an explicit service actor."));
        }

        if (resolved.Length > maximumLength)
        {
            return Result.Failure<string>(Error.Validation(
                parameterName,
                $"Actor cannot exceed {maximumLength} characters."));
        }

        return Result.Success(resolved);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
