namespace NexaOne.POM.Domain;

/// <summary>
/// Shared text limits for values copied into POM audit and traceability tables.
/// Keeping these limits in the domain prevents provider-specific truncation or DB exceptions.
/// </summary>
public static class PomStorageBoundary
{
    public const int IdentifierLength = 50;
    public const int ActorLength = 50;
    public const int DeviceIdLength = 100;
    public const int ReasonLength = 500;

    public static bool FitsRequired(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength;

    public static bool FitsOptional(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) || value.Trim().Length <= maxLength;

    /// <summary>
    /// Builds a human-readable history summary within NVARCHAR(500). The unabridged reason remains
    /// stored in the execution or exception audit row; only the derived timeline label is shortened.
    /// </summary>
    public static string HistorySummary(string prefix, string? reason = null)
    {
        var normalizedPrefix = prefix ?? string.Empty;
        if (normalizedPrefix.Length >= ReasonLength)
            return normalizedPrefix[..ReasonLength];

        var normalizedReason = reason?.Trim() ?? string.Empty;
        var remaining = ReasonLength - normalizedPrefix.Length;
        return normalizedPrefix + (normalizedReason.Length <= remaining
            ? normalizedReason
            : normalizedReason[..remaining]);
    }
}
