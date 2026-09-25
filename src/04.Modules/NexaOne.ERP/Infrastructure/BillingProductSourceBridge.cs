using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    public Task<BillingProductSource> RegisterProductSourceAsync(string userId, Guid tenantId,
        Guid organizationId, Guid movementId, BillingProductSourceInput input, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write",
            (_, session) => session.RegisterProductSource(movementId, input, ct), ct);

    public Task<BillingProductSource> GetProductSourceAsync(string userId, Guid tenantId,
        Guid organizationId, Guid movementId, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.read",
            (_, session) => session.ProductSource(movementId, required: true, ct), ct)!;

    private sealed partial class Session
    {
        internal async Task<BillingProductSource> RegisterProductSource(Guid movementId,
            BillingProductSourceInput input, CancellationToken ct)
        {
            if (movementId == Guid.Empty || input is null || input.ContactId == Guid.Empty)
                throw Failure("INVALID_BUSINESS_INPUT");
            RequireText(input.Currency, 3); RequireText(input.Description, 255);
            if (input.Currency.Any(c => c is < 'A' or > 'Z') || input.UnitPrice < 0m
                || decimal.Round(input.UnitPrice, 6) != input.UnitPrice
                || input.UnitPrice > 1_000_000_000_000m)
                throw Failure("INVALID_BUSINESS_INPUT");
            if (!await ContactExistsAsync(input.ContactId, ct)) throw Failure("BILLING_CONTACT_NOT_FOUND");

            var current = await ProductSource(movementId, required: false, ct);
            if (current is not null)
            {
                if (current.ContactId != input.ContactId || current.Currency != input.Currency
                    || current.Description != input.Description || current.UnitPrice != input.UnitPrice
                    || current.ApplyTax != input.ApplyTax || current.ApplyDiscount != input.ApplyDiscount)
                    throw Failure("BILLING_PRODUCT_SOURCE_CONFLICT");
                return current;
            }

            if (stockBilling is null) throw Failure("STOCK_MOVEMENT_NOT_FOUND");
            var movement = await stockBilling.GetMovementInTransactionAsync(transaction,
                Id(Scope.TenantId), Id(Scope.OrganizationId), movementId, ct);
            if (movement is null) throw Failure("STOCK_MOVEMENT_NOT_FOUND");
            if (!movement.BillableIssue || movement.Reversed)
                throw Failure("STOCK_MOVEMENT_NOT_BILLABLE");
            await Write("""
                INSERT INTO ERP_BILLING_PRODUCT_SOURCE
                    (TENANT_ID,ORGANIZATION_ID,MOVEMENT_ID,CONTACT_ID,OCCURRED_ON,CURRENCY,
                     DESCRIPTION,UNIT_PRICE,APPLY_TAX,APPLY_DISCOUNT,CREATED_BY,CREATED_AT_TICKS)
                VALUES (@TenantId,@OrganizationId,@Movement,@Contact,@OccurredOn,@Currency,
                        @Description,@UnitPrice,@ApplyTax,@ApplyDiscount,@CreatedBy,@CreatedAt)
                """, new { Movement = Text(movementId), Contact = Text(input.ContactId),
                    OccurredOn = Day(movement.OccurredOn), input.Currency, input.Description,
                    UnitPrice = Amount(input.UnitPrice), input.ApplyTax, input.ApplyDiscount,
                    CreatedBy = Actor.UserId, CreatedAt = clock.GetUtcNow().UtcTicks }, ct);
            return await ProductSource(movementId, required: true, ct)
                ?? throw Failure("STORAGE_CONTRACT_VIOLATION");
        }

        internal async Task<BillingProductSource?> ProductSource(Guid movementId, bool required,
            CancellationToken ct)
        {
            if (movementId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var row = await Row<ProductSourceRow>("""
                SELECT R.MOVEMENT_ID AS MovementId, R.CONTACT_ID AS ContactId,
                       R.OCCURRED_ON AS OccurredOn, R.CURRENCY AS Currency,
                       R.DESCRIPTION AS Description, R.UNIT_PRICE AS UnitPrice,
                       R.APPLY_TAX AS ApplyTax, R.APPLY_DISCOUNT AS ApplyDiscount,
                       G.DOCUMENT_ID AS InvoiceId
                  FROM ERP_BILLING_PRODUCT_SOURCE R
                  LEFT JOIN ERP_AUTOMATIC_BILLING_SOURCE S
                    ON S.TENANT_ID=R.TENANT_ID AND S.ORGANIZATION_ID=R.ORGANIZATION_ID
                   AND S.SOURCE_KIND=@SourceKind AND S.SOURCE_ID=R.MOVEMENT_ID
                  LEFT JOIN ERP_AUTOMATIC_BILLING_GENERATION G
                    ON G.TENANT_ID=S.TENANT_ID AND G.ORGANIZATION_ID=S.ORGANIZATION_ID
                   AND G.GENERATION_ID=S.GENERATION_ID
                 WHERE R.TENANT_ID=@TenantId AND R.ORGANIZATION_ID=@OrganizationId
                   AND R.MOVEMENT_ID=@Movement
                """, new { SourceKind = (int)BillingSourceKind.Product,
                    Movement = Text(movementId) }, ct);
            if (row is null)
            {
                if (required) throw Failure("BILLING_PRODUCT_SOURCE_NOT_FOUND");
                return null;
            }
            if (stockBilling is null)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            var movement = await stockBilling.GetMovementInTransactionAsync(transaction,
                Id(Scope.TenantId), Id(Scope.OrganizationId), movementId, ct)
                ?? throw Failure("STORAGE_CONTRACT_VIOLATION");
            if (!movement.BillableIssue || movement.OccurredOn != Day(row.OccurredOn))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            var occurredOn = Day(row.OccurredOn);
            var unitPrice = Amount(row.UnitPrice);
            var quantity = movement.Quantity;
            if (!ValidText(row.Currency, 3) || row.Currency.Any(c => c is < 'A' or > 'Z')
                || !ValidText(row.Description, 255) || unitPrice < 0m || quantity <= 0m)
                throw new InvalidDataException("Billing product source storage is invalid.");
            var state = row.InvoiceId is not null ? BillingProductSourceState.Invoiced
                : movement.Reversed ? BillingProductSourceState.Reversed
                : BillingProductSourceState.Eligible;
            return new(Id(row.MovementId), movement.ProductId, movement.VariantId, Id(row.ContactId),
                occurredOn, row.Currency, row.Description, unitPrice, quantity,
                row.ApplyTax, row.ApplyDiscount, state,
                OptionalId(row.InvoiceId));
        }

        private sealed class ProductSourceRow
        {
            public string MovementId { get; set; } = "";
            public string ContactId { get; set; } = "";
            public string OccurredOn { get; set; } = "";
            public string Currency { get; set; } = "";
            public string Description { get; set; } = "";
            public string UnitPrice { get; set; } = "";
            public bool ApplyTax { get; set; }
            public bool ApplyDiscount { get; set; }
            public string? InvoiceId { get; set; }
        }
    }
}
