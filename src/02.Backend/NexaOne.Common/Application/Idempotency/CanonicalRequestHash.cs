using SharedRequestHash = NexaFramework.Service.Canonical.CanonicalRequestHash;

namespace NexaOne.Application.Idempotency;

/// <summary>
/// Preserves the MES request-hash API while Framework owns NEXA-REQUEST-HASH-V1 encoding.
/// Product normalization and the choice/order of request fields remain with each caller.
/// </summary>
public static class CanonicalRequestHash
{
    public static string Compute(params object?[] values)
        => SharedRequestHash.Compute(values);

    public static string CreateId(string prefix, int hashCharacters, params object?[] values)
        => SharedRequestHash.CreateId(prefix, hashCharacters, values);
}
