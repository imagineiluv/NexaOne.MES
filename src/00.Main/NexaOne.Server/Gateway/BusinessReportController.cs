using System.Data.Common;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/erp/business-reports/{tenantId:guid}/{organizationId:guid}")]
public sealed class BusinessReportController(
    IBusinessReportBridge bridge,
    ILogger<BusinessReportController> logger) : ControllerBase
{
    [HttpGet("/api/v1/erp/business-reports/scopes/me")]
    public Task<IActionResult> ListScopes(CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListAccessibleScopesAsync(user, offset, limit, ct));

    [HttpGet("catalog")]
    public Task<IActionResult> List(Guid tenantId, Guid organizationId, CancellationToken ct)
        => Execute(user => bridge.ListAsync(user, tenantId, organizationId, ct));

    [HttpGet("{reportKey}")]
    public Task<IActionResult> Build(Guid tenantId, Guid organizationId, string reportKey,
        [FromQuery] DateOnly start, [FromQuery] DateOnly end,
        [FromQuery] BusinessReportUnit unit = BusinessReportUnit.Total,
        CancellationToken ct = default)
        => Execute(user => bridge.BuildAsync(user, tenantId, organizationId, reportKey,
            new(start, end), unit, ct));

    [HttpGet("{reportKey}/export.csv")]
    public async Task<IActionResult> Export(Guid tenantId, Guid organizationId, string reportKey,
        [FromQuery] DateOnly start, [FromQuery] DateOnly end,
        [FromQuery] BusinessReportUnit unit = BusinessReportUnit.Total,
        CancellationToken ct = default)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try
        {
            var csv = await bridge.ExportCsvAsync(userId, tenantId, organizationId,
                reportKey, new(start, end), unit, ct);
            return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
                $"{reportKey}-{start:yyyyMMdd}-{end:yyyyMMdd}-{unit.ToString().ToLowerInvariant()}.csv");
        }
        catch (BusinessException error) { return BusinessFailure(error); }
        catch (Exception error) when (IsDatabaseFailure(error)) { return StorageFailure(error); }
    }

    private async Task<IActionResult> Execute<T>(Func<string, Task<T>> action)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try { return Ok(await action(userId)); }
        catch (BusinessException error) { return BusinessFailure(error); }
        catch (Exception error) when (IsDatabaseFailure(error)) { return StorageFailure(error); }
    }

    private IActionResult BusinessFailure(BusinessException error)
    {
        if (error.Code == "BUSINESS_ACCESS_DENIED") return Forbid();
        var status = error.Code == "BUSINESS_REPORT_NOT_FOUND"
            ? StatusCodes.Status404NotFound
            : error.Code == "BUSINESS_REPORT_TOO_LARGE"
                ? StatusCodes.Status413PayloadTooLarge
                : error.Code.StartsWith("INVALID_", StringComparison.Ordinal)
                    || error.Code == "BUSINESS_REPORT_UNIT_NOT_SUPPORTED"
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status409Conflict;
        return StatusCode(status, new { code = error.Code });
    }

    private IActionResult StorageFailure(Exception error)
    {
        logger.LogError(error, "Business report persistence read failed.");
        return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Business report storage is unavailable.",
            detail: "The report was not generated. Retry the read later.");
    }

    private static bool IsDatabaseFailure(Exception error)
        => error is DbException or InvalidDataException
            || error is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException or InvalidDataException);
}
