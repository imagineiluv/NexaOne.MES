using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>Ambiguous SLS write failures are reported without replaying a business command.</summary>
internal static class SlsWriteRecovery
{
    private const string RecoveryDetail =
        "A write outcome may be unknown. Use SLS.SalesRequestById and SLS.SalesOrderById with sls:read "
        + "to inspect committed current state and related shipment evidence. These lookups are not original "
        + "write receipts. Do not retry automatically or use new IDs; reconcile the original command first.";

    public static async Task<IActionResult> Execute<T>(ControllerBase controller, ILogger logger,
        Func<Task<Result<T>>> write)
    {
        try { return (await write()).ToActionResult(); }
        catch (Exception error) when (IsStorageOrTransport(error))
        {
            return Unavailable(controller, logger, error);
        }
    }

    public static async Task<IActionResult> Execute(ControllerBase controller, ILogger logger,
        Func<Task<Result>> write)
    {
        try { return (await write()).ToActionResult(); }
        catch (Exception error) when (IsStorageOrTransport(error))
        {
            return Unavailable(controller, logger, error);
        }
    }

    private static IActionResult Unavailable(ControllerBase controller, ILogger logger, Exception error)
    {
        logger.LogError(error, "SLS write failed; outcome may be unknown.");
        return controller.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "SLS storage is unavailable.", detail: RecoveryDetail);
    }

    private static bool IsStorageOrTransport(Exception error)
        => error is DbException or IOException
            || error is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException or IOException);
}
