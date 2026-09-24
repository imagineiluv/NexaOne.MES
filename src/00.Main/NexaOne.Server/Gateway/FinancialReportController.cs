using System.Data.Common;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/erp/financial-reports/{tenantId:guid}/{organizationId:guid}")]
public sealed class FinancialReportController(
    IFinancialReportBridge bridge,
    ILogger<FinancialReportController> logger) : ControllerBase
{
    [HttpGet("/api/v1/erp/financial-reports/scopes/me")]
    public Task<IActionResult> ListScopes(CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListAccessibleScopesAsync(user, offset, limit, ct));

    [HttpGet("/api/v1/erp/cash-flow-reports/scopes/me")]
    public Task<IActionResult> ListCashFlowScopes(CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListAccessibleScopesAsync(user, offset, limit, ct));

    [HttpGet]
    public Task<IActionResult> Build(Guid tenantId, Guid organizationId,
        [FromQuery] DateOnly start, [FromQuery] DateOnly end, CancellationToken ct)
        => Execute(user => bridge.BuildAsync(user, tenantId, organizationId,
            new(start, end), ct));

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv(Guid tenantId, Guid organizationId,
        [FromQuery] DateOnly start, [FromQuery] DateOnly end, CancellationToken ct)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try
        {
            var csv = await bridge.ExportCsvAsync(userId, tenantId, organizationId,
                new(start, end), ct);
            return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
                $"financial-report-{start:yyyyMMdd}-{end:yyyyMMdd}.csv");
        }
        catch (BusinessException error) { return BusinessFailure(error); }
        catch (Exception error) when (IsDatabaseFailure(error)) { return StorageFailure(error); }
    }

    [HttpGet("/api/v1/erp/cash-flow-reports/{tenantId:guid}/{organizationId:guid}")]
    public Task<IActionResult> BuildCashFlow(Guid tenantId, Guid organizationId,
        [FromQuery] DateOnly start, [FromQuery] DateOnly end, CancellationToken ct)
        => Execute(user => bridge.BuildCashFlowAsync(user, tenantId, organizationId,
            new(start, end), ct));

    [HttpGet("/api/v1/erp/cash-flow-reports/{tenantId:guid}/{organizationId:guid}/export.csv")]
    public async Task<IActionResult> ExportCashFlowCsv(Guid tenantId, Guid organizationId,
        [FromQuery] DateOnly start, [FromQuery] DateOnly end, CancellationToken ct)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try
        {
            var csv = await bridge.ExportCashFlowCsvAsync(userId, tenantId, organizationId,
                new(start, end), ct);
            return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
                $"cash-flow-report-{start:yyyyMMdd}-{end:yyyyMMdd}.csv");
        }
        catch (BusinessException error) { return BusinessFailure(error); }
        catch (Exception error) when (IsDatabaseFailure(error)) { return StorageFailure(error); }
    }

    [HttpPost("/api/v1/erp/report-snapshots/{tenantId:guid}/{organizationId:guid}")]
    public Task<IActionResult> CreateSnapshot(Guid tenantId, Guid organizationId,
        [FromBody] CreateFinancialReportSnapshotRequest request, CancellationToken ct)
        => Execute(user => bridge.CreateSnapshotAsync(user, tenantId, organizationId,
            request.OperationId, request.Kind, request.Start, request.End, ct));

    [HttpGet("/api/v1/erp/report-snapshots/{tenantId:guid}/{organizationId:guid}")]
    public Task<IActionResult> ListSnapshots(Guid tenantId, Guid organizationId, CancellationToken ct,
        [FromQuery] FinancialReportSnapshotKind? kind = null,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListSnapshotsAsync(user, tenantId, organizationId,
            kind, offset, limit, ct));

    [HttpGet("/api/v1/erp/report-snapshots/{tenantId:guid}/{organizationId:guid}/{snapshotId:guid}")]
    public Task<IActionResult> GetSnapshot(Guid tenantId, Guid organizationId, Guid snapshotId,
        CancellationToken ct)
        => Execute(user => bridge.GetSnapshotAsync(user, tenantId, organizationId, snapshotId, ct));

    [HttpPost("/api/v1/erp/report-snapshots/{tenantId:guid}/{organizationId:guid}/{snapshotId:guid}/regenerate")]
    public Task<IActionResult> RegenerateSnapshot(Guid tenantId, Guid organizationId, Guid snapshotId,
        CancellationToken ct)
        => Execute(user => bridge.RegenerateSnapshotAsync(user, tenantId, organizationId, snapshotId, ct));

    [HttpGet("/api/v1/erp/report-snapshots/{tenantId:guid}/{organizationId:guid}/{snapshotId:guid}/export.csv")]
    public async Task<IActionResult> DownloadSnapshot(Guid tenantId, Guid organizationId, Guid snapshotId,
        CancellationToken ct)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try
        {
            var download = await bridge.DownloadSnapshotAsync(userId, tenantId, organizationId,
                snapshotId, ct);
            return File(Encoding.UTF8.GetBytes(download.Content), "text/csv; charset=utf-8",
                download.FileName);
        }
        catch (BusinessException error) { return BusinessFailure(error); }
        catch (Exception error) when (IsDatabaseFailure(error)) { return StorageFailure(error); }
    }

    [HttpGet("/api/v1/erp/report-snapshots/{tenantId:guid}/{organizationId:guid}/{snapshotId:guid}/audit")]
    public Task<IActionResult> ListSnapshotAudit(Guid tenantId, Guid organizationId, Guid snapshotId,
        CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListSnapshotAuditAsync(user, tenantId, organizationId,
            snapshotId, offset, limit, ct));

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
        var status = error.Code == "REPORT_SNAPSHOT_NOT_FOUND"
            ? StatusCodes.Status404NotFound
            : error.Code is "FINANCIAL_REPORT_TOO_LARGE" or "CASH_FLOW_REPORT_TOO_LARGE"
            ? StatusCodes.Status413PayloadTooLarge
            : error.Code.StartsWith("INVALID_", StringComparison.Ordinal) ? StatusCodes.Status400BadRequest
            : StatusCodes.Status409Conflict;
        return StatusCode(status, new { code = error.Code });
    }

    private IActionResult StorageFailure(Exception error)
    {
        logger.LogError(error, "Financial report persistence read failed.");
        return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Financial report storage is unavailable.",
            detail: "The report was not generated. Retry the read later.");
    }

    private static bool IsDatabaseFailure(Exception error)
        => error is DbException or InvalidDataException
            || error is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException or InvalidDataException);
}

public sealed record CreateFinancialReportSnapshotRequest(Guid OperationId,
    FinancialReportSnapshotKind Kind, DateOnly Start, DateOnly End);
