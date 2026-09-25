using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexaFramework.Service;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.IVT.Infrastructure;

public sealed partial class StockBridge
{
    internal const int SynchronousMasterExportRowLimit = 1_000;
    internal const int AsynchronousMasterExportRowLimit = 100_000;
    internal const int MasterExportByteLimit = 10 * 1024 * 1024;
    private const string CsvContentType = "text/csv; charset=utf-8";
    private static readonly IReadOnlyDictionary<StockMasterExportKind, IReadOnlyDictionary<string, string>> ExportFields =
        new Dictionary<StockMasterExportKind, IReadOnlyDictionary<string, string>>
        {
            [StockMasterExportKind.Products] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["productId"] = "Product ID", ["productName"] = "Product Name", ["active"] = "Active",
                ["unit"] = "Unit", ["variantId"] = "Variant ID"
            },
            [StockMasterExportKind.Warehouses] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = "Code", ["name"] = "Name", ["active"] = "Active", ["warehouseId"] = "Warehouse ID"
            }
        };

    public Task<StockMasterCsvExport> ExportMastersCsvAsync(string userId, Guid tenantId, Guid organizationId,
        StockMasterExportQuery query, CancellationToken ct = default)
    {
        var normalized = Normalize(query);
        return Run(userId, tenantId, organizationId, Permission(normalized.Kind),
            (_, session) => session.GenerateMasterExport(normalized, SynchronousMasterExportRowLimit, ct), ct);
    }

    public Task<StockMasterExportJob> QueueMasterExportAsync(string userId, Guid tenantId, Guid organizationId,
        StockMasterExportJobRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty) throw Failure("INVALID_STOCK_MASTER_EXPORT");
        var query = Normalize(new(request.Kind, request.Fields, request.IncludeInactive));
        var hash = ExportHash(query);
        return Run(userId, tenantId, organizationId, Permission(query.Kind),
            (_, session) => session.QueueMasterExport(request.OperationId, query, hash, ct), ct);
    }

    public async Task<StockMasterExportJob> GetMasterExportJobAsync(string userId, Guid tenantId, Guid organizationId,
        Guid jobId, CancellationToken ct = default)
        => (await RunForExportJob(userId, tenantId, organizationId, jobId, false, ct)).Job;

    public Task<StockMasterExportJob> RetryMasterExportAsync(string userId, Guid tenantId, Guid organizationId,
        Guid jobId, Guid expectedVersion, CancellationToken ct = default)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty
            || jobId == Guid.Empty || expectedVersion == Guid.Empty) throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock, _batchLimitSql);
            try
            {
                var stored = await session.ExportJob(jobId, false, ct) ?? throw Failure("STOCK_MASTER_EXPORT_NOT_FOUND");
                await session.Authorize(userId, [Permission((StockMasterExportKind)stored.Kind)], ct);
                if (Id(stored.Version) != expectedVersion)
                    throw new DBConcurrencyException("Stock master export retry version is stale.");
                if ((StockMasterExportState)stored.State != StockMasterExportState.Failed)
                    throw Failure("STOCK_MASTER_EXPORT_NOT_RETRYABLE");
                var nextVersion = Guid.NewGuid();
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE IVT_STOCK_MASTER_EXPORT SET VERSION=@NextVersion, STATE=0, ERROR_CODE=NULL, COMPLETED_AT_TICKS=NULL "
                    + "WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND EXPORT_ID=@Id "
                    + "AND VERSION=@ExpectedVersion AND STATE=3",
                    new { NextVersion = Text(nextVersion), TenantId = Text(tenantId), OrganizationId = Text(organizationId),
                        Id = Text(jobId), ExpectedVersion = Text(expectedVersion) }, transaction,
                    commandTimeout: _timeout, cancellationToken: ct));
                if (affected != 1) throw new DBConcurrencyException("Stock master export retry lost its compare-and-swap.");
                return session.ExportJobAccess(stored, false).Job with
                {
                    Version = nextVersion, State = StockMasterExportState.Pending, ErrorCode = null, CompletedAt = null
                };
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    public async Task<StockMasterCsvExport> DownloadMasterExportAsync(string userId, Guid tenantId,
        Guid organizationId, Guid jobId, CancellationToken ct = default)
    {
        var job = await RunForExportJob(userId, tenantId, organizationId, jobId, true, ct);
        return job.Export ?? throw Failure(job.Job.State == StockMasterExportState.Completed
            ? "STOCK_MASTER_EXPORT_ARTIFACT_MISSING" : "STOCK_MASTER_EXPORT_NOT_READY");
    }

    private Task<ExportJobAccess> RunForExportJob(string userId, Guid tenantId, Guid organizationId,
        Guid jobId, bool includeContent, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty || jobId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock, _batchLimitSql);
            try
            {
                var stored = await session.ExportJob(jobId, includeContent, ct)
                    ?? throw Failure("STOCK_MASTER_EXPORT_NOT_FOUND");
                await session.Authorize(userId, [Permission((StockMasterExportKind)stored.Kind)], ct);
                return session.ExportJobAccess(stored, includeContent);
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    internal Task<MasterExportLease?> ClaimMasterExportAsync(TimeSpan leaseDuration, CancellationToken ct)
    {
        if (leaseDuration is not { TotalSeconds: >= 10, TotalMinutes: <= 15 })
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var now = _clock.GetUtcNow();
            var parameters = new { Now = now.UtcTicks, BatchSize = 1 };
            var candidate = await connection.QuerySingleOrDefaultAsync<ExportStorageRow>(new CommandDefinition(
                "SELECT TENANT_ID AS TenantId, ORGANIZATION_ID AS OrganizationId, EXPORT_ID AS Id, VERSION AS Version, "
                + "OPERATION_ID AS OperationId, REQUESTED_BY AS RequestedBy, REQUEST_HASH AS RequestHash, EXPORT_KIND AS Kind, "
                + "FIELDS_JSON AS FieldsJson, INCLUDE_INACTIVE AS IncludeInactive, STATE AS State, REQUESTED_AT_TICKS AS RequestedAt "
                + "FROM IVT_STOCK_MASTER_EXPORT WHERE STATE=0 OR (STATE=1 AND LEASE_EXPIRES_AT_TICKS<=@Now) "
                + "ORDER BY REQUESTED_AT_TICKS, EXPORT_ID" + _batchLimitSql,
                parameters, transaction, commandTimeout: _timeout, cancellationToken: ct));
            if (candidate is null) return null;
            var leaseId = Guid.NewGuid(); var version = Guid.NewGuid();
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE IVT_STOCK_MASTER_EXPORT SET VERSION=@NextVersion, STATE=1, LEASE_ID=@LeaseId, "
                + "LEASE_EXPIRES_AT_TICKS=@LeaseExpires WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId "
                + "AND EXPORT_ID=@Id AND VERSION=@Version AND (STATE=0 OR (STATE=1 AND LEASE_EXPIRES_AT_TICKS<=@Now))",
                new { candidate.TenantId, candidate.OrganizationId, candidate.Id, candidate.Version,
                    NextVersion = Text(version), LeaseId = Text(leaseId), LeaseExpires = now.Add(leaseDuration).UtcTicks, Now = now.UtcTicks },
                transaction, commandTimeout: _timeout, cancellationToken: ct));
            if (affected != 1) throw new DBConcurrencyException("Stock master export claim lost its compare-and-swap.");
            return new MasterExportLease(Id(candidate.TenantId), Id(candidate.OrganizationId), Id(candidate.Id),
                version, leaseId, (StockMasterExportKind)candidate.Kind,
                StoredFields((StockMasterExportKind)candidate.Kind, candidate.FieldsJson), candidate.IncludeInactive);
        }, IsolationLevel.Serializable, ct);
    }

    internal async Task CompleteMasterExportAsync(MasterExportLease lease, CancellationToken ct)
        => await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(lease.TenantId), Text(lease.OrganizationId)), _memberships, _masters, _clock, _batchLimitSql);
            try
            {
                var export = await session.GenerateMasterExport(new(lease.Kind, lease.Fields, lease.IncludeInactive),
                    AsynchronousMasterExportRowLimit, ct);
                if (export.Content.Length > MasterExportByteLimit) throw Failure("STOCK_MASTER_EXPORT_TOO_LARGE");
                var completed = _clock.GetUtcNow(); var nextVersion = Guid.NewGuid();
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE IVT_STOCK_MASTER_EXPORT SET VERSION=@NextVersion, STATE=2, LEASE_ID=NULL, LEASE_EXPIRES_AT_TICKS=NULL, "
                    + "FILE_NAME=@FileName, CONTENT_TYPE=@ContentType, FILE_SIZE=@FileSize, CONTENT=@Content, ROW_COUNT=@RowCount, "
                    + "COMPLETED_AT_TICKS=@CompletedAt WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId "
                    + "AND EXPORT_ID=@Id AND VERSION=@Version AND STATE=1 AND LEASE_ID=@LeaseId",
                    new { NextVersion = Text(nextVersion), export.FileName, export.ContentType, FileSize = export.Content.Length,
                        export.Content, export.RowCount, CompletedAt = completed.UtcTicks, TenantId = Text(lease.TenantId),
                        OrganizationId = Text(lease.OrganizationId), Id = Text(lease.Id), Version = Text(lease.Version), LeaseId = Text(lease.LeaseId) },
                    transaction, commandTimeout: _timeout, cancellationToken: ct));
                if (affected != 1) throw new DBConcurrencyException("Stock master export completion lost its lease.");
                return true;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);

    internal async Task FailMasterExportAsync(MasterExportLease lease, string errorCode, CancellationToken ct)
    {
        if (!ValidText(errorCode, 100)) errorCode = "STOCK_MASTER_EXPORT_FAILED";
        await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE IVT_STOCK_MASTER_EXPORT SET VERSION=@NextVersion, STATE=3, LEASE_ID=NULL, LEASE_EXPIRES_AT_TICKS=NULL, "
                + "ERROR_CODE=@ErrorCode, COMPLETED_AT_TICKS=@CompletedAt WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId "
                + "AND EXPORT_ID=@Id AND VERSION=@Version AND STATE=1 AND LEASE_ID=@LeaseId",
                new { NextVersion = Text(Guid.NewGuid()), ErrorCode = errorCode, CompletedAt = _clock.GetUtcNow().UtcTicks,
                    TenantId = Text(lease.TenantId), OrganizationId = Text(lease.OrganizationId), Id = Text(lease.Id),
                    Version = Text(lease.Version), LeaseId = Text(lease.LeaseId) }, transaction,
                commandTimeout: _timeout, cancellationToken: ct));
            if (affected != 1) throw new DBConcurrencyException("Stock master export failure settlement lost its lease.");
            return true;
        }, IsolationLevel.Serializable, ct);
    }

    private static StockMasterExportQuery Normalize(StockMasterExportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ExportFields.TryGetValue(query.Kind, out var allowed) || query.Fields is null
            || query.Fields.Count == 0 || query.Fields.Count > allowed.Count)
            throw Failure("INVALID_STOCK_MASTER_EXPORT");
        var fields = query.Fields.ToArray();
        if (fields.Any(field => field is null || !allowed.ContainsKey(field))
            || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw Failure("INVALID_STOCK_MASTER_EXPORT_FIELD");
        return new(query.Kind, Array.AsReadOnly(fields), query.IncludeInactive);
    }

    private static string Permission(StockMasterExportKind kind) => kind switch
    {
        StockMasterExportKind.Products => "stock.read",
        StockMasterExportKind.Warehouses => "stock.warehouse.read",
        _ => throw Failure("INVALID_STOCK_MASTER_EXPORT")
    };

    private static string ExportHash(StockMasterExportQuery query)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = (int)query.Kind, fields = query.Fields, includeInactive = query.IncludeInactive
        })));

    private static IReadOnlyList<string> StoredFields(StockMasterExportKind kind, string json)
    {
        try
        {
            var fields = JsonSerializer.Deserialize<string[]>(json) ?? throw new JsonException();
            return Normalize(new(kind, fields)).Fields;
        }
        catch (Exception error) when (error is JsonException or BusinessException)
        {
            throw new InvalidDataException("Stock export storage contains invalid fields.", error);
        }
    }

    private sealed partial class Session
    {
        internal async Task<StockMasterExportJob> QueueMasterExport(Guid operationId, StockMasterExportQuery query,
            string hash, CancellationToken ct)
        {
            var prior = await ExportJobByOperation(operationId, false, ct);
            if (prior is not null)
            {
                if (prior.RequestedBy != Actor.UserId || prior.RequestHash != hash)
                    throw Failure("STOCK_MASTER_EXPORT_OPERATION_CONFLICT");
                return ExportJobAccess(prior, false).Job with { Replayed = true };
            }
            var id = Guid.NewGuid(); var version = Guid.NewGuid(); var requestedAt = clock.GetUtcNow();
            await Write("INSERT INTO IVT_STOCK_MASTER_EXPORT (TENANT_ID, ORGANIZATION_ID, EXPORT_ID, VERSION, OPERATION_ID, "
                + "REQUESTED_BY, REQUEST_HASH, EXPORT_KIND, FIELDS_JSON, INCLUDE_INACTIVE, STATE, REQUESTED_AT_TICKS) "
                + "VALUES (@TenantId,@OrganizationId,@Id,@Version,@Operation,@Actor,@Hash,@Kind,@Fields,@IncludeInactive,0,@RequestedAt)",
                new { Id = Text(id), Version = Text(version), Operation = Text(operationId), Actor = Actor.UserId, Hash = hash,
                    Kind = (int)query.Kind, Fields = JsonSerializer.Serialize(query.Fields), query.IncludeInactive,
                    RequestedAt = requestedAt.UtcTicks }, ct);
            return new(id, version, operationId, query.Kind, query.Fields, query.IncludeInactive,
                StockMasterExportState.Pending, null, null, null, requestedAt, null);
        }

        internal Task<ExportStorageRow?> ExportJob(Guid id, bool includeContent, CancellationToken ct)
            => Row<ExportStorageRow>(ExportSelect(includeContent) + " WHERE " + ScopeWhere + " AND EXPORT_ID=@Id",
                new { Id = Text(id) }, ct);

        private Task<ExportStorageRow?> ExportJobByOperation(Guid operationId, bool includeContent, CancellationToken ct)
            => Row<ExportStorageRow>(ExportSelect(includeContent) + " WHERE " + ScopeWhere + " AND OPERATION_ID=@Operation",
                new { Operation = Text(operationId) }, ct);

        internal ExportJobAccess ExportJobAccess(ExportStorageRow row, bool includeContent)
        {
            if (!Enum.IsDefined(typeof(StockMasterExportKind), row.Kind)
                || !Enum.IsDefined(typeof(StockMasterExportState), row.State))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            IReadOnlyList<string> fields;
            try { fields = Normalize(new((StockMasterExportKind)row.Kind, JsonSerializer.Deserialize<string[]>(row.FieldsJson) ?? [] )).Fields; }
            catch (Exception error) when (error is JsonException or BusinessException)
            { throw new InvalidDataException("Stock export storage contains invalid fields.", error); }
            var job = new StockMasterExportJob(Id(row.Id), Id(row.Version), Id(row.OperationId),
                (StockMasterExportKind)row.Kind, fields, row.IncludeInactive, (StockMasterExportState)row.State,
                row.ExportedRows, row.FileName, row.ErrorCode, At(row.RequestedAt), row.CompletedAt.HasValue ? At(row.CompletedAt.Value) : null);
            StockMasterCsvExport? export = null;
            if (includeContent && job.State == StockMasterExportState.Completed)
            {
                if (row.Content is null || row.FileName is null || row.ContentType != CsvContentType
                    || row.FileSize != row.Content.Length || row.ExportedRows is null || row.Content.Length > MasterExportByteLimit)
                    throw Failure("STOCK_MASTER_EXPORT_ARTIFACT_MISSING");
                export = new(row.FileName, row.ContentType, row.Content, row.ExportedRows.Value);
            }
            return new(job, export);
        }

        internal async Task<StockMasterCsvExport> GenerateMasterExport(StockMasterExportQuery query, int rowLimit,
            CancellationToken ct)
        {
            var rows = query.Kind == StockMasterExportKind.Products
                ? await ProductExportRows(query.IncludeInactive, rowLimit, ct)
                : await WarehouseExportRows(query.IncludeInactive, rowLimit, ct);
            var headers = query.Fields.Select(field => ExportFields[query.Kind][field]).ToArray();
            var text = new StringBuilder();
            AppendCsvRow(text, headers, false);
            foreach (var row in rows)
                AppendCsvRow(text, query.Fields.Select(field => row[field]), true);
            var encoding = new UTF8Encoding(true, true);
            var body = encoding.GetBytes(text.ToString());
            var content = new byte[encoding.GetPreamble().Length + body.Length];
            encoding.GetPreamble().CopyTo(content, 0); body.CopyTo(content, encoding.GetPreamble().Length);
            if (content.Length > MasterExportByteLimit) throw Failure("STOCK_MASTER_EXPORT_TOO_LARGE");
            var noun = query.Kind == StockMasterExportKind.Products ? "products" : "warehouses";
            return new($"inventory-{noun}-{Scope.TenantId.Replace("-", "", StringComparison.Ordinal)}-"
                + $"{Scope.OrganizationId.Replace("-", "", StringComparison.Ordinal)}.csv", CsvContentType, content, rows.Count);
        }

        private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ProductExportRows(bool includeInactive,
            int rowLimit, CancellationToken ct)
        {
            var stored = (await connection.QueryAsync<EnrolledProduct>(Command("SELECT " + ProductColumns
                + " FROM IVT_STOCK_PRODUCT WHERE " + ScopeWhere + " ORDER BY MASTER_PRODUCT_ID, VARIANT_ID", null, ct))).ToArray();
            if (stored.LongLength > AsynchronousMasterExportRowLimit) throw Failure("STOCK_MASTER_EXPORT_TOO_LARGE");
            var masterById = new Dictionary<string, ProductDto>(StringComparer.Ordinal);
            foreach (var batch in stored.Select(row => row.MasterId).Distinct(StringComparer.Ordinal).Chunk(ListBatchSize))
            {
                var found = await masters.FindProductsAsync(transaction, batch, ct);
                if (found is null || found.Count != batch.Length) throw Failure("STORAGE_CONTRACT_VIOLATION");
                foreach (var master in found)
                    if (master is null || !masterById.TryAdd(master.ProductId, master)) throw Failure("STORAGE_CONTRACT_VIOLATION");
            }
            var result = new List<IReadOnlyDictionary<string, string>>();
            foreach (var row in stored)
            {
                if (!masterById.TryGetValue(row.MasterId, out var master)) throw Failure("STORAGE_CONTRACT_VIOLATION");
                var active = master.ValidState == "Valid" && master.Unit == row.Unit;
                if (!includeInactive && !active) continue;
                result.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["productId"] = master.ProductId, ["productName"] = master.ProductName,
                    ["active"] = active ? "true" : "false", ["unit"] = row.Unit, ["variantId"] = row.VariantId
                });
                if (result.Count > rowLimit) throw Failure("STOCK_MASTER_EXPORT_TOO_LARGE");
            }
            return result;
        }

        private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> WarehouseExportRows(bool includeInactive,
            int rowLimit, CancellationToken ct)
        {
            var filter = includeInactive ? string.Empty : " AND IS_ACTIVE=1";
            var rows = (await connection.QueryAsync<WarehouseRow>(Command("SELECT " + WarehouseColumns
                + " FROM IVT_STOCK_WAREHOUSE WHERE " + ScopeWhere + filter + " ORDER BY CODE, WAREHOUSE_ID", null, ct))).ToArray();
            if (rows.LongLength > rowLimit) throw Failure("STOCK_MASTER_EXPORT_TOO_LARGE");
            return rows.Select(row => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = row.Code, ["name"] = row.Name, ["active"] = row.Active ? "true" : "false", ["warehouseId"] = row.Id
            }).ToArray();
        }

        private static string ExportSelect(bool content) => "SELECT TENANT_ID AS TenantId, ORGANIZATION_ID AS OrganizationId, "
            + "EXPORT_ID AS Id, VERSION AS Version, OPERATION_ID AS OperationId, REQUESTED_BY AS RequestedBy, REQUEST_HASH AS RequestHash, "
            + "EXPORT_KIND AS Kind, FIELDS_JSON AS FieldsJson, INCLUDE_INACTIVE AS IncludeInactive, STATE AS State, FILE_NAME AS FileName, "
            + "CONTENT_TYPE AS ContentType, FILE_SIZE AS FileSize, " + (content ? "CONTENT" : "NULL") + " AS Content, ROW_COUNT AS ExportedRows, "
            + "ERROR_CODE AS ErrorCode, REQUESTED_AT_TICKS AS RequestedAt, COMPLETED_AT_TICKS AS CompletedAt FROM IVT_STOCK_MASTER_EXPORT";
    }

    private static void AppendCsvRow(StringBuilder target, IEnumerable<string> values, bool protectFormula)
    {
        var first = true;
        foreach (var original in values)
        {
            if (!first) target.Append(','); first = false;
            var value = original ?? string.Empty;
            var candidate = value.AsSpan().TrimStart();
            if (protectFormula && value.Length > 0 && (value[0] is '\t' or '\r'
                || candidate.Length > 0 && candidate[0] is '=' or '+' or '-' or '@')) value = "'" + value;
            target.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        target.Append("\r\n");
    }

    private static DateTimeOffset At(long ticks)
    {
        try { return new DateTimeOffset(ticks, TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException error) { throw new InvalidDataException("Stock export storage contains an invalid timestamp.", error); }
    }

    internal sealed record MasterExportLease(Guid TenantId, Guid OrganizationId, Guid Id, Guid Version,
        Guid LeaseId, StockMasterExportKind Kind, IReadOnlyList<string> Fields, bool IncludeInactive);
    private sealed record ExportJobAccess(StockMasterExportJob Job, StockMasterCsvExport? Export);

    private sealed class ExportStorageRow
    {
        public string TenantId { get; set; } = "";
        public string OrganizationId { get; set; } = "";
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string RequestedBy { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public int Kind { get; set; }
        public string FieldsJson { get; set; } = "";
        public bool IncludeInactive { get; set; }
        public int State { get; set; }
        public string? FileName { get; set; }
        public string? ContentType { get; set; }
        public int? FileSize { get; set; }
        public byte[]? Content { get; set; }
        public int? ExportedRows { get; set; }
        public string? ErrorCode { get; set; }
        public long RequestedAt { get; set; }
        public long? CompletedAt { get; set; }
    }
}

/// <summary>Claims durable master export jobs and stores bounded CSV artifacts for later authorized download.</summary>
public sealed class StockMasterExportWorker : BackgroundService
{
    private readonly StockBridge _bridge;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _leaseDuration;
    private readonly ILogger _logger;

    public StockMasterExportWorker(StockBridge bridge, IConfiguration configuration, ILogger? logger = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        ArgumentNullException.ThrowIfNull(configuration);
        _enabled = configuration.GetValue("Worker:Ivt:StockMasterExport:Enabled", true);
        _interval = TimeSpan.FromSeconds(configuration.GetValue("Worker:Ivt:StockMasterExport:IntervalSeconds", 2));
        _leaseDuration = TimeSpan.FromSeconds(configuration.GetValue("Worker:Ivt:StockMasterExport:LeaseSeconds", 300));
        if (_interval is not { TotalMilliseconds: >= 100, TotalMinutes: <= 5 }) throw new InvalidOperationException("Invalid stock export interval.");
        if (_leaseDuration is not { TotalSeconds: >= 10, TotalMinutes: <= 15 }) throw new InvalidOperationException("Invalid stock export lease.");
        _logger = logger ?? NullLogger.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = await RunOnceAsync(stoppingToken);
            if (!worked) await Task.Delay(_interval, stoppingToken);
        }
    }

    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        StockBridge.MasterExportLease? lease;
        try { lease = await _bridge.ClaimMasterExportAsync(_leaseDuration, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger.LogError(error, "Stock master export claim failed.");
            return false;
        }
        if (lease is null) return false;
        try { await _bridge.CompleteMasterExportAsync(lease, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger.LogError(error, "Stock master export {ExportId} failed.", lease.Id);
            try
            {
                var code = error is BusinessException business ? business.Code : "STOCK_MASTER_EXPORT_FAILED";
                await _bridge.FailMasterExportAsync(lease, code, ct);
            }
            catch (Exception settlementError) when (settlementError is not OperationCanceledException)
            {
                _logger.LogError(settlementError, "Stock master export {ExportId} failure could not be settled.", lease.Id);
            }
        }
        return true;
    }
}
