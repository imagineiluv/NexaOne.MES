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
[Route("api/v1/erp/billing/{tenantId:guid}/{organizationId:guid}")]
public sealed class BillingController(IBillingBridge bridge, ILogger<BillingController> logger) : ControllerBase
{
    [HttpGet("/api/v1/erp/billing/scopes/me")]
    public Task<IActionResult> ListScopes(CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListAccessibleScopesAsync(user, offset, limit, ct));

    [HttpGet("contacts")]
    public Task<IActionResult> ListContacts(Guid tenantId, Guid organizationId, CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListContactsAsync(user, tenantId, organizationId, offset, limit, ct));

    [HttpPut("contacts/{customerId}")]
    public Task<IActionResult> EnrollContact(Guid tenantId, Guid organizationId, string customerId, CancellationToken ct)
        => Execute(user => bridge.EnrollContactAsync(user, tenantId, organizationId, customerId, ct));

    [HttpPost("documents")]
    public Task<IActionResult> CreateDocument(Guid tenantId, Guid organizationId, [FromBody] DocumentCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateDocumentAsync(user, tenantId, organizationId, command.OperationId, command.Kind, command.Input, ct));

    [HttpPost("documents/automatic")]
    public Task<IActionResult> GenerateAutomaticInvoice(Guid tenantId, Guid organizationId,
        [FromBody] AutomaticDocumentCreate command, CancellationToken ct)
        => Execute(user => bridge.GenerateAutomaticInvoiceAsync(user, tenantId, organizationId,
            command.OperationId, command.Request, ct));

    [HttpGet("documents")]
    public Task<IActionResult> ListDocuments(Guid tenantId, Guid organizationId, [FromQuery] BillingKind? kind, [FromQuery] BillingStatus? status,
        [FromQuery] Guid? contactId, CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListDocumentsAsync(user, tenantId, organizationId, kind, status, contactId, offset, limit, ct));

    [HttpGet("documents/{id:guid}")]
    public Task<IActionResult> GetDocument(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetDocumentAsync(user, tenantId, organizationId, id, ct));

    [HttpPut("documents/{id:guid}")]
    public Task<IActionResult> UpdateDocument(Guid tenantId, Guid organizationId, Guid id, [FromBody] DocumentChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateDocumentAsync(user, tenantId, organizationId, id, command.Version, command.Input, ct));

    [HttpPost("documents/{id:guid}/sent")]
    public Task<IActionResult> MarkSent(Guid tenantId, Guid organizationId, Guid id, [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.MarkSentAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost("documents/{id:guid}/decision")]
    public Task<IActionResult> DecideEstimate(Guid tenantId, Guid organizationId, Guid id, [FromBody] DecisionCommand command, CancellationToken ct)
        => Execute(user => bridge.DecideEstimateAsync(user, tenantId, organizationId, id, command.Version, command.Accepted, ct));

    [HttpPost("documents/{id:guid}/conversion")]
    public Task<IActionResult> ConvertEstimate(Guid tenantId, Guid organizationId, Guid id, [FromBody] ConversionCommand command, CancellationToken ct)
        => Execute(user => bridge.ConvertEstimateAsync(user, tenantId, organizationId, command.OperationId, id, command.Version, ct));

    [HttpPost("documents/{id:guid}/void")]
    public Task<IActionResult> VoidDocument(Guid tenantId, Guid organizationId, Guid id, [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.VoidDocumentAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpGet("documents/{id:guid}/payments")]
    public Task<IActionResult> ListPayments(Guid tenantId, Guid organizationId, Guid id, [FromQuery] PaymentState? state,
        CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListPaymentsAsync(user, tenantId, organizationId, id, state, offset, limit, ct));

    [HttpPost("documents/{id:guid}/payments")]
    public Task<IActionResult> RecordPayment(Guid tenantId, Guid organizationId, Guid id, [FromBody] PaymentCommand command, CancellationToken ct)
        // The route document is authoritative; a body naming another document is a caller error, not a silent redirect.
        => Execute(user => command.Input.DocumentId == id
            ? bridge.RecordPaymentAsync(user, tenantId, organizationId, command.OperationId, command.Input, ct)
            : throw new BusinessException("INVALID_BUSINESS_INPUT"));

    [HttpGet("payments/{id:guid}")]
    public Task<IActionResult> GetPayment(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetPaymentAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("payments/{id:guid}/cancel")]
    public Task<IActionResult> CancelPayment(Guid tenantId, Guid organizationId, Guid id, [FromBody] CancelCommand command, CancellationToken ct)
        => Execute(user => bridge.CancelPaymentAsync(user, tenantId, organizationId, id, command.Version, command.Reason, ct));

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
            logger.LogError(error, "Billing persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "Billing storage is unavailable.",
                detail: "A write outcome may be unknown. Read the document or payment by ID before retrying; "
                    + "for document creation, conversion or payment recording, retry with the same operation ID and original payload.");
        }
    }

    public sealed record DocumentCreate(Guid OperationId, BillingKind Kind, BillingDocumentInput Input);
    public sealed record AutomaticDocumentCreate(Guid OperationId, AutomaticBillingRequest Request);
    public sealed record DocumentChange(Guid Version, BillingDocumentInput Input);
    public sealed record VersionedCommand(Guid Version);
    public sealed record DecisionCommand(Guid Version, bool Accepted);
    public sealed record ConversionCommand(Guid OperationId, Guid Version);
    public sealed record PaymentCommand(Guid OperationId, PaymentInput Input);
    public sealed record CancelCommand(Guid Version, string? Reason);
}
