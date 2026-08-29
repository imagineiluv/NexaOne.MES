using System.Diagnostics;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>
/// Converts a configured validity duration into a process-local monotonic deadline. The caller supplies the
/// timestamp captured before the remote lease operation starts, so network/DB latency can only shorten local
/// authority and can never extend it beyond the configured TTL.
/// </summary>
internal static class FdcMonotonicDeadline
{
    public static long FromOperationStart(long operationStartedTimestamp, TimeSpan validity)
    {
        if (validity <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(validity), validity, "Validity must be positive.");

        var timestampDelta = (long)Math.Floor(validity.TotalSeconds * Stopwatch.Frequency);
        return checked(operationStartedTimestamp + timestampDelta);
    }

    public static long FromNow(TimeSpan validity) =>
        FromOperationStart(Stopwatch.GetTimestamp(), validity);

    public static bool IsExpired(long deadlineTimestamp) =>
        deadlineTimestamp <= Stopwatch.GetTimestamp();

    public static TimeSpan Remaining(long deadlineTimestamp)
    {
        var now = Stopwatch.GetTimestamp();
        return deadlineTimestamp <= now
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(now, deadlineTimestamp);
    }
}
