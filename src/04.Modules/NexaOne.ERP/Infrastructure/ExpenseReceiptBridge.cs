using System.Security.Cryptography;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    private const int MaxReceiptBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> ReceiptContentTypes = new(StringComparer.OrdinalIgnoreCase)
        { "application/pdf", "image/jpeg", "image/png", "image/webp" };

    public Task<ExpenseReceipt?> GetReceiptAsync(string userId, Guid tenantId, Guid organizationId,
        Guid expenseId, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.read",
            async (_, _, session) => (await session.GetReceiptAsync(expenseId, includeContent: false, ct))?.Receipt, ct);

    public Task<ExpenseReceiptDownload> DownloadReceiptAsync(string userId, Guid tenantId, Guid organizationId,
        Guid expenseId, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.read",
            async (_, _, session) => await session.GetReceiptAsync(expenseId, includeContent: true, ct)
                ?? throw Failure("EXPENSE_RECEIPT_NOT_FOUND"), ct);

    public Task<ExpenseReceipt> PutReceiptAsync(string userId, Guid tenantId, Guid organizationId,
        Guid expenseId, Guid? expectedVersion, string fileName, string contentType, byte[] content,
        CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.write",
            (_, _, session) => session.PutReceiptAsync(expenseId, expectedVersion, fileName, contentType, content, ct), ct);

    public async Task DeleteReceiptAsync(string userId, Guid tenantId, Guid organizationId,
        Guid expenseId, Guid version, CancellationToken ct = default)
    {
        _ = await RunExpense(userId, tenantId, organizationId, "expense.write",
            async (_, _, session) => { await session.DeleteReceiptAsync(expenseId, version, ct); return true; }, ct);
    }

    private sealed partial class Session
    {
        internal async Task<ExpenseReceiptDownload?> GetReceiptAsync(Guid expenseId, bool includeContent, CancellationToken ct)
        {
            EnsureExecution(Scope, ct);
            if (expenseId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            return await GetReceiptCoreAsync(expenseId, includeContent, ct);
        }

        private async Task<ExpenseReceiptDownload?> GetReceiptCoreAsync(Guid expenseId, bool includeContent, CancellationToken ct)
        {
            var row = await Row<ReceiptRow>("SELECT RECEIPT_ID AS Id,EXPENSE_ID AS ExpenseId,VERSION AS Version,FILE_NAME AS FileName,CONTENT_TYPE AS ContentType,FILE_SIZE AS Size,SHA256 AS Sha256,"
                + (includeContent ? "CONTENT" : "NULL") + " AS Content,UPLOADED_BY AS UploadedBy,UPLOADED_AT_TICKS AS UploadedAt FROM ERP_EXPENSE_RECEIPT WHERE "
                + ScopeWhere + " AND EXPENSE_ID=@ExpenseId", new { ExpenseId = Text(expenseId) }, ct);
            if (row is null) return null;
            var receipt = Receipt(row);
            var content = row.Content ?? [];
            if (includeContent && (content.LongLength != receipt.Size
                || !HasReceiptSignature(receipt.ContentType, content)
                || !Convert.ToHexString(SHA256.HashData(content)).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            return new(receipt, content);
        }

        internal async Task<ExpenseReceipt> PutReceiptAsync(Guid expenseId, Guid? expectedVersion,
            string fileName, string contentType, byte[] content, CancellationToken ct)
        {
            EnsureExecution(Scope, ct);
            if (expenseId == Guid.Empty || content is null || content.Length is < 1 or > MaxReceiptBytes)
                throw Failure("INVALID_BUSINESS_INPUT");
            fileName = fileName?.Trim() ?? ""; contentType = contentType?.Trim() ?? "";
            if (!ValidText(fileName, 255) || !IsSafeReceiptFileName(fileName)
                || !ReceiptContentTypes.Contains(contentType) || !HasReceiptSignature(contentType, content))
                throw Failure("INVALID_BUSINESS_INPUT");
            _ = await FindExpenseAsync(expenseId, ct) ?? throw Failure("EXPENSE_NOT_FOUND");
            var current = await GetReceiptCoreAsync(expenseId, false, ct);
            if (current is null && expectedVersion is not null || current is not null && current.Receipt.Version != expectedVersion)
                throw Failure("BUSINESS_VERSION_CONFLICT");
            var next = new ExpenseReceipt(current?.Receipt.Id ?? Guid.NewGuid(), expenseId, Scope, Guid.NewGuid(),
                fileName, contentType, content.LongLength, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                Actor.UserId, clock.GetUtcNow());
            var values = new { Id = Text(next.Id), ExpenseId = Text(expenseId), Version = Text(next.Version), next.FileName,
                next.ContentType, next.Size, next.Sha256, Content = content, next.UploadedBy, UploadedAt = next.UploadedAt.UtcTicks,
                Previous = Text(expectedVersion) };
            await Write(current is null
                ? "INSERT INTO ERP_EXPENSE_RECEIPT (TENANT_ID,ORGANIZATION_ID,RECEIPT_ID,EXPENSE_ID,VERSION,FILE_NAME,CONTENT_TYPE,FILE_SIZE,SHA256,CONTENT,UPLOADED_BY,UPLOADED_AT_TICKS) VALUES (@TenantId,@OrganizationId,@Id,@ExpenseId,@Version,@FileName,@ContentType,@Size,@Sha256,@Content,@UploadedBy,@UploadedAt)"
                : "UPDATE ERP_EXPENSE_RECEIPT SET VERSION=@Version,FILE_NAME=@FileName,CONTENT_TYPE=@ContentType,FILE_SIZE=@Size,SHA256=@Sha256,CONTENT=@Content,UPLOADED_BY=@UploadedBy,UPLOADED_AT_TICKS=@UploadedAt WHERE " + ScopeWhere + " AND EXPENSE_ID=@ExpenseId AND VERSION=@Previous", values, ct);
            await AppendAuditAsync(new(Guid.NewGuid(), Actor, "expense-receipt", next.Id,
                current is null ? "uploaded" : "replaced", expectedVersion, next.Version, next.UploadedAt), ct);
            return next;
        }

        internal async Task DeleteReceiptAsync(Guid expenseId, Guid version, CancellationToken ct)
        {
            EnsureExecution(Scope, ct);
            if (expenseId == Guid.Empty || version == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var current = await GetReceiptCoreAsync(expenseId, false, ct) ?? throw Failure("EXPENSE_RECEIPT_NOT_FOUND");
            if (current.Receipt.Version != version) throw Failure("BUSINESS_VERSION_CONFLICT");
            await Write("DELETE FROM ERP_EXPENSE_RECEIPT WHERE " + ScopeWhere + " AND EXPENSE_ID=@ExpenseId AND VERSION=@Version",
                new { ExpenseId = Text(expenseId), Version = Text(version) }, ct);
            await AppendAuditAsync(new(Guid.NewGuid(), Actor, "expense-receipt", current.Receipt.Id,
                "deleted", version, Guid.NewGuid(), clock.GetUtcNow()), ct);
        }

        private ExpenseReceipt Receipt(ReceiptRow row)
        {
            var value = new ExpenseReceipt(Id(row.Id), Id(row.ExpenseId), Scope, Id(row.Version), row.FileName,
                row.ContentType, row.Size, row.Sha256, row.UploadedBy, new DateTimeOffset(row.UploadedAt, TimeSpan.Zero));
            if (!ValidText(value.FileName, 255) || !IsSafeReceiptFileName(value.FileName)
                || !ValidText(value.ContentType, 100) || !ReceiptContentTypes.Contains(value.ContentType)
                || value.Size is < 1 or > MaxReceiptBytes
                || value.Sha256.Length != 64 || !value.Sha256.All(Uri.IsHexDigit) || !ValidText(value.UploadedBy, 50))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            return value;
        }

        private static bool IsSafeReceiptFileName(string fileName)
            => fileName is not "." and not ".."
                && !fileName.Contains('/')
                && !fileName.Contains('\\')
                && !fileName.Any(char.IsControl);

        private static bool HasReceiptSignature(string contentType, ReadOnlySpan<byte> content)
            => contentType.ToLowerInvariant() switch
            {
                "application/pdf" => content.StartsWith("%PDF-"u8),
                "image/jpeg" => content.Length >= 3
                    && content[0] == 0xff && content[1] == 0xd8 && content[2] == 0xff,
                "image/png" => content.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "image/webp" => content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8)
                    && content.Slice(8, 4).SequenceEqual("WEBP"u8),
                _ => false
            };

        private sealed class ReceiptRow
        {
            public string Id { get; set; } = "";
            public string ExpenseId { get; set; } = "";
            public string Version { get; set; } = "";
            public string FileName { get; set; } = "";
            public string ContentType { get; set; } = "";
            public long Size { get; set; }
            public string Sha256 { get; set; } = "";
            public byte[]? Content { get; set; }
            public string UploadedBy { get; set; } = "";
            public long UploadedAt { get; set; }
        }
    }
}
