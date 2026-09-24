using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    public Task<FinancialReportSnapshot> CreateSnapshotAsync(string userId, Guid tenantId,
        Guid organizationId, Guid operationId, FinancialReportSnapshotKind kind, DateOnly start,
        DateOnly end, CancellationToken ct = default)
    {
        if (operationId == Guid.Empty || !ValidKind(kind)) throw Failure("INVALID_BUSINESS_INPUT");
        return RunSnapshot(userId, tenantId, organizationId,
            session => session.CreateSnapshot(operationId, kind, start, end, ct), ct);
    }

    public Task<FinancialReportSnapshot> GetSnapshotAsync(string userId, Guid tenantId,
        Guid organizationId, Guid snapshotId, CancellationToken ct = default)
    {
        if (snapshotId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
        return RunSnapshot(userId, tenantId, organizationId,
            session => session.GetSnapshot(snapshotId, ct), ct);
    }

    public Task<BusinessPage<FinancialReportSnapshotSummary>> ListSnapshotsAsync(string userId,
        Guid tenantId, Guid organizationId, FinancialReportSnapshotKind? kind = null, int offset = 0,
        int limit = 50, CancellationToken ct = default)
    {
        if (kind.HasValue && !ValidKind(kind.Value) || offset < 0 || limit is < 1 or > 100)
            throw Failure("INVALID_BUSINESS_INPUT");
        return RunSnapshot(userId, tenantId, organizationId,
            session => session.ListSnapshots(kind, offset, limit, ct), ct);
    }

    public Task<FinancialReportSnapshotComparison> RegenerateSnapshotAsync(string userId,
        Guid tenantId, Guid organizationId, Guid snapshotId, CancellationToken ct = default)
    {
        if (snapshotId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
        return RunSnapshot(userId, tenantId, organizationId,
            session => session.RegenerateSnapshot(snapshotId, ct), ct);
    }

    public Task<FinancialReportSnapshotDownload> DownloadSnapshotAsync(string userId,
        Guid tenantId, Guid organizationId, Guid snapshotId, CancellationToken ct = default)
    {
        if (snapshotId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
        return RunSnapshot(userId, tenantId, organizationId,
            session => session.DownloadSnapshot(snapshotId, ct), ct);
    }

    public Task<BusinessPage<FinancialReportSnapshotAuditEntry>> ListSnapshotAuditAsync(string userId,
        Guid tenantId, Guid organizationId, Guid snapshotId, int offset = 0, int limit = 50,
        CancellationToken ct = default)
    {
        if (snapshotId == Guid.Empty || offset < 0 || limit is < 1 or > 100)
            throw Failure("INVALID_BUSINESS_INPUT");
        return RunSnapshot(userId, tenantId, organizationId,
            session => session.ListSnapshotAudit(snapshotId, offset, limit, ct), ct);
    }

    private Task<T> RunSnapshot<T>(string userId, Guid tenantId, Guid organizationId,
        Func<Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock);
            try
            {
                await session.Authorize(userId, "financial-report.read", ct);
                var result = await action(session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private static bool ValidKind(FinancialReportSnapshotKind kind)
        => kind is FinancialReportSnapshotKind.Financial or FinancialReportSnapshotKind.CashFlow;

    private sealed partial class Session
    {
        internal async Task<FinancialReportSnapshot> CreateSnapshot(Guid operationId,
            FinancialReportSnapshotKind kind, DateOnly start, DateOnly end, CancellationToken ct)
        {
            var existing = await SnapshotRow(operationId, ct);
            if (existing is not null)
            {
                var replay = Snapshot(existing);
                if (replay.Summary.Kind != kind || replay.Summary.Start != start || replay.Summary.End != end
                    || replay.Summary.CreatedBy != Actor.UserId)
                    throw Failure("REPORT_SNAPSHOT_OPERATION_CONFLICT");
                return replay;
            }

            FinancialReportSnapshot snapshot;
            string json;
            string csv;
            if (kind == FinancialReportSnapshotKind.Financial)
            {
                var report = await new FinancialReportService(this, this, clock)
                    .BuildAsync(Actor, new(start, end), ct);
                csv = FinancialReportService.ExportCsv(report);
                json = JsonSerializer.Serialize(report);
                snapshot = Wrap(operationId, report);
            }
            else
            {
                var report = await new CashFlowReportService(this, this, clock)
                    .BuildAsync(Actor, new(start, end), ct);
                csv = CashFlowReportService.ExportCsv(report);
                json = JsonSerializer.Serialize(report);
                snapshot = Wrap(operationId, report);
            }

            var summary = snapshot.Summary;
            await Write("""
                INSERT INTO ERP_FINANCIAL_REPORT_SNAPSHOT
                    (TENANT_ID, ORGANIZATION_ID, SNAPSHOT_ID, REPORT_KIND, PERIOD_START, PERIOD_END,
                     REPORT_JSON, CSV_CONTENT, CONTENT_HASH, CREATED_BY, CREATED_AT_TICKS)
                VALUES (@TenantId, @OrganizationId, @Id, @Kind, @Start, @End,
                        @Json, @Csv, @Hash, @CreatedBy, @CreatedAt)
                """, new
            {
                Id = Text(summary.Id), Kind = (int)summary.Kind, Start = Day(summary.Start),
                End = Day(summary.End), Json = json, Csv = csv, Hash = summary.ContentHash,
                summary.CreatedBy, CreatedAt = summary.CreatedAt.UtcTicks
            }, ct);
            await AppendSnapshotAudit(summary.Id, FinancialReportSnapshotAuditAction.Created,
                summary.ContentHash, null, summary.CreatedAt, ct);
            return snapshot;
        }

        internal async Task<FinancialReportSnapshot> GetSnapshot(Guid snapshotId, CancellationToken ct)
            => Snapshot(await SnapshotRow(snapshotId, ct) ?? throw Failure("REPORT_SNAPSHOT_NOT_FOUND"));

        internal async Task<BusinessPage<FinancialReportSnapshotSummary>> ListSnapshots(
            FinancialReportSnapshotKind? kind, int offset, int limit, CancellationToken ct)
        {
            const string filter = " AND (@Kind IS NULL OR REPORT_KIND=@Kind)";
            var values = new
            {
                Kind = kind.HasValue ? (int?)kind.Value : null,
                Offset = offset,
                End = (long)offset + limit
            };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_FINANCIAL_REPORT_SNAPSHOT WHERE "
                + ScopeWhere + filter, values, ct);
            var rows = await Rows<SnapshotRow>("SELECT * FROM (SELECT " + SnapshotSummaryColumns
                + ", ROW_NUMBER() OVER (ORDER BY CREATED_AT_TICKS DESC,SNAPSHOT_ID DESC) AS RowNumber "
                + "FROM ERP_FINANCIAL_REPORT_SNAPSHOT WHERE " + ScopeWhere + filter
                + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber", values, ct);
            return new(Array.AsReadOnly(rows.Select(Summary).ToArray()), total);
        }

        internal async Task<FinancialReportSnapshotComparison> RegenerateSnapshot(Guid snapshotId,
            CancellationToken ct)
        {
            var stored = Snapshot(await SnapshotRow(snapshotId, ct)
                ?? throw Failure("REPORT_SNAPSHOT_NOT_FOUND"));
            string currentHash;
            DateTimeOffset comparedAt;
            if (stored.Summary.Kind == FinancialReportSnapshotKind.Financial)
            {
                var current = await new FinancialReportService(this, this, clock).BuildAsync(Actor,
                    new(stored.Summary.Start, stored.Summary.End), ct);
                currentHash = Hash(current);
                comparedAt = current.GeneratedAt;
            }
            else
            {
                var current = await new CashFlowReportService(this, this, clock).BuildAsync(Actor,
                    new(stored.Summary.Start, stored.Summary.End), ct);
                currentHash = Hash(current);
                comparedAt = current.GeneratedAt;
            }
            var matches = string.Equals(stored.Summary.ContentHash, currentHash, StringComparison.Ordinal);
            await AppendSnapshotAudit(snapshotId, FinancialReportSnapshotAuditAction.Regenerated,
                currentHash, matches, comparedAt, ct);
            return new(snapshotId, matches, stored.Summary.ContentHash, currentHash, comparedAt);
        }

        internal async Task<FinancialReportSnapshotDownload> DownloadSnapshot(Guid snapshotId,
            CancellationToken ct)
        {
            var snapshot = Snapshot(await SnapshotRow(snapshotId, ct)
                ?? throw Failure("REPORT_SNAPSHOT_NOT_FOUND"));
            var at = clock.GetUtcNow();
            await AppendSnapshotAudit(snapshotId, FinancialReportSnapshotAuditAction.Downloaded,
                snapshot.Summary.ContentHash, null, at, ct);
            var prefix = snapshot.Summary.Kind == FinancialReportSnapshotKind.Financial
                ? "financial-report" : "cash-flow-report";
            return new($"{prefix}-{snapshot.Summary.Start:yyyyMMdd}-{snapshot.Summary.End:yyyyMMdd}-{snapshotId:D}.csv",
                snapshot.Summary.Kind == FinancialReportSnapshotKind.Financial
                    ? FinancialReportService.ExportCsv(snapshot.FinancialReport!)
                    : CashFlowReportService.ExportCsv(snapshot.CashFlowReport!));
        }

        internal async Task<BusinessPage<FinancialReportSnapshotAuditEntry>> ListSnapshotAudit(
            Guid snapshotId, int offset, int limit, CancellationToken ct)
        {
            if (await Scalar<long>("SELECT COUNT(*) FROM ERP_FINANCIAL_REPORT_SNAPSHOT WHERE "
                + ScopeWhere + " AND SNAPSHOT_ID=@Snapshot", new { Snapshot = Text(snapshotId) }, ct) != 1)
                throw Failure("REPORT_SNAPSHOT_NOT_FOUND");
            var values = new { Snapshot = Text(snapshotId), Offset = offset, End = (long)offset + limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_FINANCIAL_REPORT_SNAPSHOT_AUDIT WHERE "
                + ScopeWhere + " AND SNAPSHOT_ID=@Snapshot", values, ct);
            var rows = await Rows<SnapshotAuditRow>("SELECT * FROM (SELECT AUDIT_ID AS Id, "
                + "SNAPSHOT_ID AS SnapshotId, ACTION AS Action, USER_ID AS UserId, AT_TICKS AS At, "
                + "OBSERVED_CONTENT_HASH AS ObservedContentHash, MATCHED AS Matched, "
                + "ROW_NUMBER() OVER (ORDER BY AT_TICKS DESC,AUDIT_ID DESC) AS RowNumber "
                + "FROM ERP_FINANCIAL_REPORT_SNAPSHOT_AUDIT WHERE " + ScopeWhere
                + " AND SNAPSHOT_ID=@Snapshot) AS page WHERE RowNumber>@Offset AND RowNumber<=@End "
                + "ORDER BY RowNumber", values, ct);
            return new(Array.AsReadOnly(rows.Select(Audit).ToArray()), total);
        }

        private const string SnapshotSummaryColumns = "SNAPSHOT_ID AS Id, REPORT_KIND AS Kind, "
            + "PERIOD_START AS Start, PERIOD_END AS End, CONTENT_HASH AS ContentHash, "
            + "CREATED_BY AS CreatedBy, CREATED_AT_TICKS AS CreatedAt";
        private const string SnapshotColumns = SnapshotSummaryColumns
            + ", REPORT_JSON AS Json, CSV_CONTENT AS Csv";

        private Task<SnapshotRow?> SnapshotRow(Guid id, CancellationToken ct)
            => Row<SnapshotRow>("SELECT " + SnapshotColumns + " FROM ERP_FINANCIAL_REPORT_SNAPSHOT WHERE "
                + ScopeWhere + " AND SNAPSHOT_ID=@Id", new { Id = Text(id) }, ct);

        private FinancialReportSnapshot Snapshot(SnapshotRow row)
        {
            var summary = Summary(row);
            try
            {
                if (summary.Kind == FinancialReportSnapshotKind.Financial)
                {
                    var report = JsonSerializer.Deserialize<FinancialReport>(row.Json)
                        ?? throw new InvalidDataException("Report snapshot payload is null.");
                    Validate(report, summary, row.Csv);
                    return new(summary, report, null);
                }
                var cashFlow = JsonSerializer.Deserialize<CashFlowReport>(row.Json)
                    ?? throw new InvalidDataException("Cash-flow snapshot payload is null.");
                Validate(cashFlow, summary, row.Csv);
                return new(summary, null, cashFlow);
            }
            catch (Exception error) when (error is JsonException or BusinessException
                or ArgumentException or InvalidOperationException or OverflowException)
            {
                throw new InvalidDataException("Report snapshot payload is malformed or violates its contract.", error);
            }
        }

        private FinancialReportSnapshotSummary Summary(SnapshotRow row)
        {
            if (!Enum.IsDefined(typeof(FinancialReportSnapshotKind), row.Kind))
                throw new InvalidDataException("Report snapshot kind is invalid.");
            var hash = StoredHash(row.ContentHash);
            var start = Day(row.Start);
            var end = Day(row.End);
            var createdAt = new DateTimeOffset(row.CreatedAt, TimeSpan.Zero);
            if (start == default || end < start || end.DayNumber - start.DayNumber > 365
                || createdAt == default)
                throw new InvalidDataException("Report snapshot period or creation time is invalid.");
            return new(Id(row.Id), (FinancialReportSnapshotKind)row.Kind, start, end,
                createdAt, Text(Id(row.CreatedBy)), hash);
        }

        private FinancialReportSnapshotAuditEntry Audit(SnapshotAuditRow row)
        {
            var isRegeneration = row.Action == (int)FinancialReportSnapshotAuditAction.Regenerated;
            if (!Enum.IsDefined(typeof(FinancialReportSnapshotAuditAction), row.Action)
                || isRegeneration != row.Matched.HasValue)
                throw new InvalidDataException("Report snapshot audit action is invalid.");
            var at = new DateTimeOffset(row.At, TimeSpan.Zero);
            if (at == default) throw new InvalidDataException("Report snapshot audit time is invalid.");
            return new(Id(row.Id), Id(row.SnapshotId), (FinancialReportSnapshotAuditAction)row.Action,
                Text(Id(row.UserId)), at,
                StoredHash(row.ObservedContentHash), row.Matched);
        }

        private async Task AppendSnapshotAudit(Guid snapshotId,
            FinancialReportSnapshotAuditAction action, string observedHash, bool? matches,
            DateTimeOffset at, CancellationToken ct)
        {
            await Write("""
                INSERT INTO ERP_FINANCIAL_REPORT_SNAPSHOT_AUDIT
                    (TENANT_ID, ORGANIZATION_ID, AUDIT_ID, SNAPSHOT_ID, ACTION, USER_ID,
                     AT_TICKS, OBSERVED_CONTENT_HASH, MATCHED)
                VALUES (@TenantId, @OrganizationId, @Id, @Snapshot, @Action, @UserId,
                        @At, @Hash, @Matched)
                """, new
            {
                Id = Text(Guid.NewGuid()), Snapshot = Text(snapshotId), Action = (int)action,
                Actor.UserId, At = at.UtcTicks, Hash = StoredHash(observedHash), Matched = matches
            }, ct);
        }

        private FinancialReportSnapshot Wrap(Guid id, FinancialReport report)
            => new(new(id, FinancialReportSnapshotKind.Financial, report.Period.Start, report.Period.End,
                report.GeneratedAt, Actor.UserId, Hash(report)), report, null);

        private FinancialReportSnapshot Wrap(Guid id, CashFlowReport report)
            => new(new(id, FinancialReportSnapshotKind.CashFlow, report.Period.Start, report.Period.End,
                report.GeneratedAt, Actor.UserId, Hash(report)), null, report);

        private void Validate(FinancialReport report, FinancialReportSnapshotSummary summary, string csv)
        {
            var rendered = FinancialReportService.ExportCsv(report);
            if (report.Scope != Scope || report.Period.Start != summary.Start || report.Period.End != summary.End
                || report.GeneratedAt != summary.CreatedAt || rendered != csv || Hash(report) != summary.ContentHash)
                throw new InvalidDataException("Financial report snapshot content does not match its metadata.");
        }

        private void Validate(CashFlowReport report, FinancialReportSnapshotSummary summary, string csv)
        {
            var rendered = CashFlowReportService.ExportCsv(report);
            if (report.Scope != Scope || report.Period.Start != summary.Start || report.Period.End != summary.End
                || report.GeneratedAt != summary.CreatedAt || rendered != csv || Hash(report) != summary.ContentHash)
                throw new InvalidDataException("Cash-flow report snapshot content does not match its metadata.");
        }

        private static string Hash(FinancialReport report)
            => Sha256(FinancialReportService.ExportCsv(report with { GeneratedAt = DateTimeOffset.UnixEpoch }));

        private static string Hash(CashFlowReport report)
            => Sha256(CashFlowReportService.ExportCsv(report with { GeneratedAt = DateTimeOffset.UnixEpoch }));

        private static string Sha256(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private static string StoredHash(string value)
            => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9'
                or >= 'A' and <= 'F') ? value
                : throw new InvalidDataException("Report snapshot hash is invalid.");
    }

    private sealed class SnapshotRow
    {
        public string Id { get; set; } = "";
        public int Kind { get; set; }
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
        public string Json { get; set; } = "";
        public string Csv { get; set; } = "";
        public string ContentHash { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public long CreatedAt { get; set; }
    }

    private sealed class SnapshotAuditRow
    {
        public string Id { get; set; } = "";
        public string SnapshotId { get; set; } = "";
        public int Action { get; set; }
        public string UserId { get; set; } = "";
        public long At { get; set; }
        public string ObservedContentHash { get; set; } = "";
        public bool? Matched { get; set; }
    }
}
