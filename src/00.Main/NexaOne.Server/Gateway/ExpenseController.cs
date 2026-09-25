using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/erp/expenses/{tenantId:guid}/{organizationId:guid}")]
public sealed class ExpenseController(IExpenseBridge bridge, ILogger<ExpenseController> logger) : ControllerBase
{
    private const long MaxReceiptBytes = 10L * 1024 * 1024;
    [HttpGet("/api/v1/erp/expenses/scopes/me")]
    public Task<IActionResult> ListScopes(CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListAccessibleScopesAsync(user, offset, limit, ct));

    [HttpGet("categories")]
    public Task<IActionResult> ListCategories(Guid tenantId, Guid organizationId,
        [FromQuery] ExpenseDirectoryQuery query, CancellationToken ct)
        => Execute(user => bridge.ListCategoriesAsync(user, tenantId, organizationId, query, ct));

    [HttpGet("employees")]
    public Task<IActionResult> ListEmployees(Guid tenantId, Guid organizationId, CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListEmployeesAsync(user, tenantId, organizationId, offset, limit, ct));

    [HttpGet("tags")]
    public Task<IActionResult> ListTags(Guid tenantId, Guid organizationId,
        [FromQuery] ExpenseTagQuery query, CancellationToken ct)
        => Execute(user => bridge.ListTagsAsync(user, tenantId, organizationId, query, ct));

    [HttpPost("tags")]
    public Task<IActionResult> CreateTag(Guid tenantId, Guid organizationId,
        [FromBody] ExpenseTagInput input, CancellationToken ct)
        => Execute(user => bridge.CreateTagAsync(user, tenantId, organizationId, input, ct));

    [HttpGet("tags/{id:guid}")]
    public Task<IActionResult> GetTag(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetTagAsync(user, tenantId, organizationId, id, ct));

    [HttpPut("tags/{id:guid}")]
    public Task<IActionResult> UpdateTag(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] TagChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateTagAsync(user, tenantId, organizationId, id,
            command.Version, command.Input, ct));

    [HttpPost("tags/{id:guid}/active")]
    public Task<IActionResult> SetTagActive(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] ActiveChange command, CancellationToken ct)
        => Execute(user => bridge.SetTagActiveAsync(user, tenantId, organizationId, id,
            command.Version, command.Active, ct));

    [HttpPost("categories")]
    public Task<IActionResult> CreateCategory(Guid tenantId, Guid organizationId,
        [FromBody] ExpenseCategoryInput input, CancellationToken ct)
        => Execute(user => bridge.CreateCategoryAsync(user, tenantId, organizationId, input, ct));

    [HttpGet("categories/{id:guid}")]
    public Task<IActionResult> GetCategory(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetCategoryAsync(user, tenantId, organizationId, id, ct));

    [HttpPut("categories/{id:guid}")]
    public Task<IActionResult> UpdateCategory(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] CategoryChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateCategoryAsync(user, tenantId, organizationId, id,
            command.Version, command.Input, ct));

    [HttpPost("categories/{id:guid}/active")]
    public Task<IActionResult> SetCategoryActive(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] ActiveChange command, CancellationToken ct)
        => Execute(user => bridge.SetCategoryActiveAsync(user, tenantId, organizationId, id,
            command.Version, command.Active, ct));

    [HttpGet("vendors")]
    public Task<IActionResult> ListVendors(Guid tenantId, Guid organizationId,
        [FromQuery] ExpenseDirectoryQuery query, CancellationToken ct)
        => Execute(user => bridge.ListVendorsAsync(user, tenantId, organizationId, query, ct));

    [HttpPost("vendors")]
    public Task<IActionResult> CreateVendor(Guid tenantId, Guid organizationId,
        [FromBody] ExpenseVendorInput input, CancellationToken ct)
        => Execute(user => bridge.CreateVendorAsync(user, tenantId, organizationId, input, ct));

    [HttpGet("vendors/{id:guid}")]
    public Task<IActionResult> GetVendor(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetVendorAsync(user, tenantId, organizationId, id, ct));

    [HttpPut("vendors/{id:guid}")]
    public Task<IActionResult> UpdateVendor(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VendorChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateVendorAsync(user, tenantId, organizationId, id,
            command.Version, command.Input, ct));

    [HttpPost("vendors/{id:guid}/active")]
    public Task<IActionResult> SetVendorActive(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] ActiveChange command, CancellationToken ct)
        => Execute(user => bridge.SetVendorActiveAsync(user, tenantId, organizationId, id,
            command.Version, command.Active, ct));

    [HttpGet]
    public Task<IActionResult> ListExpenses(Guid tenantId, Guid organizationId,
        [FromQuery] ExpenseQuery query, CancellationToken ct)
        => Execute(user => bridge.ListExpensesAsync(user, tenantId, organizationId, query, ct));

    [HttpPost]
    public Task<IActionResult> CreateExpense(Guid tenantId, Guid organizationId,
        [FromBody] ExpenseCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateExpenseAsync(user, tenantId, organizationId,
            command.OperationId, command.Input, ct));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> GetExpense(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetExpenseAsync(user, tenantId, organizationId, id, ct));

    [HttpPut("{id:guid}")]
    public Task<IActionResult> UpdateExpense(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] ExpenseChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateExpenseAsync(user, tenantId, organizationId, id,
            command.Version, command.Input, ct));

    [HttpPost("{id:guid}/invoiced")]
    public Task<IActionResult> MarkInvoiced(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.MarkInvoicedAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost("{id:guid}/paid")]
    public Task<IActionResult> MarkPaid(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.MarkPaidAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost("{id:guid}/reimbursement")]
    public Task<IActionResult> Reimburse(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] ReimbursementCommand command, CancellationToken ct)
        => Execute(user => bridge.ReimburseExpenseAsync(user, tenantId, organizationId,
            command.OperationId, id, command.Version, command.PaidAt, command.Reference, ct));

    [HttpPost("{id:guid}/payouts")]
    public Task<IActionResult> QueuePayout(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] PayoutCommand command, CancellationToken ct)
        => Execute(user => bridge.QueuePayoutAsync(user, tenantId, organizationId, command.OperationId,
            new(id, command.ExpenseVersion, command.ProviderKey), ct));

    [HttpGet("payouts/{payoutId:guid}")]
    public Task<IActionResult> GetPayout(Guid tenantId, Guid organizationId, Guid payoutId, CancellationToken ct)
        => Execute(user => bridge.GetPayoutAsync(user, tenantId, organizationId, payoutId, ct));

    [HttpGet("payouts")]
    public Task<IActionResult> ListPayouts(Guid tenantId, Guid organizationId, CancellationToken ct,
        [FromQuery] Guid? expenseId = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListPayoutsAsync(user, tenantId, organizationId,
            expenseId, offset, limit, ct));

    [HttpGet("payouts/failed")]
    public Task<IActionResult> ListFailedPayouts(Guid tenantId, Guid organizationId, CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListFailedPayoutsAsync(user, tenantId, organizationId, offset, limit, ct));

    [HttpPost("payouts/{payoutId:guid}/cancel")]
    public Task<IActionResult> CancelPayout(Guid tenantId, Guid organizationId, Guid payoutId,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.CancelPayoutAsync(user, tenantId, organizationId,
            payoutId, command.Version, ct));

    [HttpPost("payouts/{payoutId:guid}/failed/retry")]
    public Task<IActionResult> RetryFailedPayout(Guid tenantId, Guid organizationId, Guid payoutId,
        [FromBody] PayoutFailureCommand command, CancellationToken ct)
        => Execute(user => bridge.RetryFailedPayoutAsync(user, tenantId, organizationId,
            command.OperationId, payoutId, command.Version, ct));

    [HttpPost("payouts/{payoutId:guid}/failed/discard")]
    public Task<IActionResult> DiscardFailedPayout(Guid tenantId, Guid organizationId, Guid payoutId,
        [FromBody] PayoutFailureCommand command, CancellationToken ct)
        => Execute(user => bridge.DiscardFailedPayoutAsync(user, tenantId, organizationId,
            command.OperationId, payoutId, command.Version, ct));

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] CancelCommand command, CancellationToken ct)
        => Execute(user => bridge.CancelExpenseAsync(user, tenantId, organizationId,
            id, command.Version, command.Reason, ct));

    [HttpGet("{id:guid}/receipt")]
    public async Task<IActionResult> GetReceipt(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
    {
        var result = await Execute(user => bridge.GetReceiptAsync(user, tenantId, organizationId, id, ct));
        return result is OkObjectResult { Value: null } ? NotFound(new { code = "EXPENSE_RECEIPT_NOT_FOUND" }) : result;
    }

    [HttpPut("{id:guid}/receipt")]
    [RequestSizeLimit(MaxReceiptBytes + 64 * 1024)]
    public async Task<IActionResult> PutReceipt(Guid tenantId, Guid organizationId, Guid id,
        [FromForm] IFormFile? file, [FromForm] Guid? version, CancellationToken ct)
    {
        if (file is null || file.Length is < 1 or > MaxReceiptBytes)
            return BadRequest(new { code = "INVALID_BUSINESS_INPUT" });
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream((int)file.Length);
        await input.CopyToAsync(buffer, ct);
        if (buffer.Length != file.Length || buffer.Length > MaxReceiptBytes)
            return BadRequest(new { code = "INVALID_BUSINESS_INPUT" });
        return await Execute(user => bridge.PutReceiptAsync(user, tenantId, organizationId, id,
            version, file.FileName, file.ContentType, buffer.ToArray(), ct));
    }

    [HttpGet("{id:guid}/receipt/download")]
    public async Task<IActionResult> DownloadReceipt(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
    {
        var result = await Execute(user => bridge.DownloadReceiptAsync(user, tenantId, organizationId, id, ct));
        if (result is not OkObjectResult { Value: ExpenseReceiptDownload download }) return result;
        return File(download.Content, download.Receipt.ContentType, download.Receipt.FileName);
    }

    [HttpDelete("{id:guid}/receipt")]
    public Task<IActionResult> DeleteReceipt(Guid tenantId, Guid organizationId, Guid id,
        [FromQuery] Guid version, CancellationToken ct)
        => Execute(async user =>
        {
            await bridge.DeleteReceiptAsync(user, tenantId, organizationId, id, version, ct);
            return new ReceiptDeleted(id, version);
        });

    [HttpPost("{id:guid}/invoice-link")]
    public Task<IActionResult> LinkInvoice(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] InvoiceLinkCommand command, CancellationToken ct)
        => Execute(user => bridge.LinkInvoiceAsync(user, tenantId, organizationId, command.OperationId,
            id, command.ExpenseVersion, command.InvoiceId, command.InvoiceVersion, command.Description, ct));

    [HttpPost("{id:guid}/invoice-unlink")]
    public Task<IActionResult> UnlinkInvoice(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] InvoiceUnlinkCommand command, CancellationToken ct)
        => Execute(user => bridge.UnlinkInvoiceAsync(user, tenantId, organizationId, command.OperationId,
            id, command.ExpenseVersion, command.InvoiceId, command.InvoiceVersion, ct));

    private async Task<IActionResult> Execute<T>(Func<string, Task<T>> action)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try { return Ok(await action(userId)); }
        catch (BusinessException error)
        {
            if (error.Code == "BUSINESS_ACCESS_DENIED") return Forbid();
            var status = error.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? 404
                : error.Code.StartsWith("INVALID_", StringComparison.Ordinal) ? 400 : 409;
            return StatusCode(status, new { code = error.Code });
        }
        catch (DBConcurrencyException) { return Conflict(new { code = "BUSINESS_VERSION_CONFLICT" }); }
        catch (Exception error) when (error is DbException
            || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException))
        {
            logger.LogError(error, "Expense persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "Expense storage is unavailable.",
                detail: "A write outcome may be unknown. Read the affected record before retrying; "
                    + "for expense creation, reimbursement, invoice linking or unlinking, retry with the same operation ID and original payload.");
        }
    }

    public sealed record CategoryChange(Guid Version, ExpenseCategoryInput Input);
    public sealed record VendorChange(Guid Version, ExpenseVendorInput Input);
    public sealed record TagChange(Guid Version, ExpenseTagInput Input);
    public sealed record ActiveChange(Guid Version, bool Active);
    public sealed record ExpenseCreate(Guid OperationId, ExpenseInput Input);
    public sealed record ExpenseChange(Guid Version, ExpenseInput Input);
    public sealed record VersionedCommand(Guid Version);
    public sealed record ReimbursementCommand(Guid OperationId, Guid Version, DateTimeOffset PaidAt, string? Reference);
    public sealed record PayoutCommand(Guid OperationId, Guid ExpenseVersion, string ProviderKey);
    public sealed record PayoutFailureCommand(Guid OperationId, Guid Version);
    public sealed record CancelCommand(Guid Version, string? Reason);
    public sealed record InvoiceLinkCommand(Guid OperationId, Guid ExpenseVersion, Guid InvoiceId,
        Guid InvoiceVersion, string? Description);
    public sealed record InvoiceUnlinkCommand(Guid OperationId, Guid ExpenseVersion, Guid InvoiceId,
        Guid InvoiceVersion);
    public sealed record ReceiptDeleted(Guid ExpenseId, Guid Version);
}
