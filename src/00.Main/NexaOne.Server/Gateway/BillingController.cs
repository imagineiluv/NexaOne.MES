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
public sealed class BillingController(IBillingBridge bridge, IWebHostEnvironment environment,
    ILogger<BillingController> logger) : ControllerBase
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

    [HttpPost("documents/{invoiceId:guid}/credits")]
    public Task<IActionResult> CreateCreditNote(Guid tenantId, Guid organizationId, Guid invoiceId,
        [FromBody] CreditNoteCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateCreditNoteAsync(user, tenantId, organizationId,
            command.OperationId, invoiceId, command.Input, ct));

    [HttpPut("credit-notes/{id:guid}")]
    public Task<IActionResult> UpdateCreditNote(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] DocumentChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateCreditNoteAsync(user, tenantId, organizationId,
            id, command.Version, command.Input, ct));

    [HttpPost("credit-notes/{id:guid}/issue")]
    public Task<IActionResult> IssueCreditNote(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.IssueCreditNoteAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost("credit-notes/{id:guid}/void")]
    public Task<IActionResult> VoidCreditNote(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.VoidCreditNoteAsync(user, tenantId, organizationId, id, command.Version, ct));

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

    [HttpGet("documents/{id:guid}/pdf")]
    public Task<IActionResult> DownloadPdf(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => ExecutePdf(user => bridge.GetDocumentViewAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("documents/{id:guid}/shares")]
    public Task<IActionResult> CreateShare(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] ShareCreate command, CancellationToken ct)
        => Execute(async user =>
        {
            var secret = await bridge.CreateShareAsync(user, tenantId, organizationId,
                command.OperationId, id, command.ExpiresAt, ct);
            return new ShareCreated(secret.Link,
                secret.Token is null ? null : PublicUrl(secret.Token), secret.Token is not null);
        });

    [HttpGet("documents/{id:guid}/shares")]
    public Task<IActionResult> ListShares(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListSharesAsync(user, tenantId, organizationId, id, offset, limit, ct));

    [HttpPost("shares/{shareId:guid}/revoke")]
    public Task<IActionResult> RevokeShare(Guid tenantId, Guid organizationId, Guid shareId,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.RevokeShareAsync(user, tenantId, organizationId, shareId, command.Version, ct));

    [HttpGet("shares/{shareId:guid}/access")]
    public Task<IActionResult> ListShareAccess(Guid tenantId, Guid organizationId, Guid shareId,
        CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListShareAccessAsync(user, tenantId, organizationId,
            shareId, offset, limit, ct));

    [HttpGet("shares/{shareId:guid}/deliveries")]
    public Task<IActionResult> ListShareDeliveries(Guid tenantId, Guid organizationId, Guid shareId,
        CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListShareDeliveriesAsync(user, tenantId, organizationId,
            shareId, offset, limit, ct));

    [HttpPost("shares/{shareId:guid}/deliveries")]
    public Task<IActionResult> QueueShareDelivery(Guid tenantId, Guid organizationId, Guid shareId,
        [FromBody] ShareDeliveryCommand command, CancellationToken ct)
        => Execute(user => bridge.QueueShareDeliveryAsync(user, tenantId, organizationId,
            command.OperationId, shareId, command.Token, command.TemplateId, command.ProfileId,
            command.Recipient, PublicUrl(command.Token), command.ScheduledAt, ct));

    [AllowAnonymous]
    [HttpGet("/api/v1/erp/billing/public/{token}/pdf")]
    public async Task<IActionResult> OpenPublicPdf(string token, CancellationToken ct)
    {
        try
        {
            var opened = await bridge.OpenPublicShareAsync(token,
                HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
            ApplyPrivateDownloadHeaders();
            return Pdf(opened.View);
        }
        catch (BusinessException error)
        {
            return error.Code == "BILLING_SHARE_NOT_FOUND"
                ? NotFound(new { code = error.Code })
                : Map(error);
        }
        catch (Exception error) when (IsDatabase(error))
        {
            logger.LogError(error, "Public billing-share access failed.");
            return Problem(statusCode: 503, title: "Billing storage is unavailable.");
        }
    }

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
        catch (BusinessException error) { return Map(error); }
        catch (DBConcurrencyException) { return Conflict(new { code = "BUSINESS_VERSION_CONFLICT" }); }
        catch (Exception error) when (IsDatabase(error))
        {
            logger.LogError(error, "Billing persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "Billing storage is unavailable.",
                detail: "A write outcome may be unknown. Read the document or payment by ID before retrying; "
                    + "for document or credit-note creation, conversion or payment recording, retry with the same operation ID and original payload.");
        }
    }

    private async Task<IActionResult> ExecutePdf(Func<string, Task<BillingDocumentView>> action)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try { ApplyPrivateDownloadHeaders(); return Pdf(await action(userId)); }
        catch (BusinessException error) { return Map(error); }
        catch (Exception error) when (IsDatabase(error))
        {
            logger.LogError(error, "Billing PDF read failed.");
            return Problem(statusCode: 503, title: "Billing storage is unavailable.");
        }
    }

    private IActionResult Pdf(BillingDocumentView view)
    {
        var font = environment.WebRootFileProvider.GetFileInfo("fonts/Pretendard-Regular.ttf").PhysicalPath;
        var bytes = BillingPdfRenderer.Render(view,
            font ?? throw new InvalidOperationException("The billing PDF font is unavailable."));
        return File(bytes, "application/pdf", $"billing-{view.Document.Kind.ToString().ToLowerInvariant()}-{view.Document.Number}.pdf");
    }

    private string PublicUrl(string token)
        => Url.ActionLink(nameof(OpenPublicPdf), values: new { token }, protocol: Request.Scheme,
               host: Request.Host.ToUriComponent())
           ?? throw new InvalidOperationException("The public billing URL could not be created.");

    private void ApplyPrivateDownloadHeaders()
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private IActionResult Map(BusinessException error)
    {
        if (error.Code == "BUSINESS_ACCESS_DENIED") return Forbid();
        var status = error.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? 404
            : error.Code.StartsWith("INVALID_", StringComparison.Ordinal) ? 400 : 409;
        return StatusCode(status, new { code = error.Code });
    }

    private static bool IsDatabase(Exception error)
        => error is DbException || error is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException);

    public sealed record DocumentCreate(Guid OperationId, BillingKind Kind, BillingDocumentInput Input);
    public sealed record CreditNoteCreate(Guid OperationId, BillingDocumentInput Input);
    public sealed record AutomaticDocumentCreate(Guid OperationId, AutomaticBillingRequest Request);
    public sealed record DocumentChange(Guid Version, BillingDocumentInput Input);
    public sealed record VersionedCommand(Guid Version);
    public sealed record DecisionCommand(Guid Version, bool Accepted);
    public sealed record ConversionCommand(Guid OperationId, Guid Version);
    public sealed record PaymentCommand(Guid OperationId, PaymentInput Input);
    public sealed record CancelCommand(Guid Version, string? Reason);
    public sealed record ShareCreate(Guid OperationId, DateTimeOffset ExpiresAt);
    public sealed record ShareCreated(BillingShareLink Link, string? PublicUrl, bool SecretReturned);
    public sealed record ShareDeliveryCommand(Guid OperationId, string Token, Guid TemplateId,
        Guid ProfileId, string Recipient, DateTimeOffset? ScheduledAt = null);
}
