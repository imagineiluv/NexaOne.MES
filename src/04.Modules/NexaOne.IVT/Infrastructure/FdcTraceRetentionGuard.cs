using System.Globalization;
using System.Text.RegularExpressions;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Fdc;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.IVT.Infrastructure;

/// <summary>
/// IVT가 소유한 활성 TRACE binding과 durable ingestion cursor에서 FDC 보존정리용 전역
/// low-watermark를 계산한다. cursor가 아직 없는 binding은 EFFECTIVE_FROM부터 보호한다.
/// </summary>
internal sealed class FdcTraceRetentionGuard : QueryRepository, IFdcTraceRetentionGuard
{
    private static readonly Regex CanonicalSqliteUtcTimestamp = new(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] CanonicalSqliteUtcFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.f",
        "yyyy-MM-dd HH:mm:ss.ff",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ffff",
        "yyyy-MM-dd HH:mm:ss.fffff",
        "yyyy-MM-dd HH:mm:ss.ffffff",
        "yyyy-MM-dd HH:mm:ss.fffffff",
    ];

    private readonly DatabaseProviderKind _providerKind;

    public FdcTraceRetentionGuard(EesDataSource dataSource) : base(dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _providerKind = dataSource.Provider.Kind;
    }

    public async Task<DateTime?> GetLowWatermarkAsync(CancellationToken ct = default)
    {
        if (_providerKind == DatabaseProviderKind.Sqlite)
            return await GetSqliteLowWatermarkAsync(ct);

        const string sql = """
            SELECT COUNT(*) AS ActiveBindingCount,
                   MIN(CASE
                       WHEN C.BINDING_ID IS NULL
                         OR C.LAST_COLLECTED_AT < B.EFFECTIVE_FROM
                           THEN B.EFFECTIVE_FROM
                       ELSE C.LAST_COLLECTED_AT
                   END) AS LowWatermark
              FROM IVT_TRACE_CONSUMPTION_BINDING B
              LEFT JOIN IVT_TRACE_INGESTION_CURSOR C
                ON C.BINDING_ID = B.BINDING_ID
             WHERE B.IS_ACTIVE = 1
            """;
        var row = await QueryFirstOrDefaultAsync<LowWatermarkRow>(sql, null, ct)
            ?? throw new InvalidOperationException(
                "IVT TRACE retention guard did not return an aggregate row.");
        if (row.ActiveBindingCount == 0) return null;
        if (row.LowWatermark is null)
        {
            throw new InvalidOperationException(
                "IVT TRACE retention guard found an active binding without a durable low-watermark.");
        }

        return row.LowWatermark.Value.Kind == DateTimeKind.Utc
            ? row.LowWatermark.Value
            : DateTime.SpecifyKind(row.LowWatermark.Value, DateTimeKind.Utc);
    }

    private async Task<DateTime?> GetSqliteLowWatermarkAsync(CancellationToken ct)
    {
        // SQLite MIN(TEXT) follows lexical rather than instant order. Read the small active-binding
        // configuration set, reject non-canonical values, and compare parsed UTC instants so a
        // T/Z/offset or invalid calendar value can never move a retention cutoff forward.
        const string sql = """
            SELECT B.BINDING_ID AS BindingId,
                   B.EFFECTIVE_FROM AS EffectiveFromText,
                   C.BINDING_ID AS CursorBindingId,
                   C.LAST_COLLECTED_AT AS LastCollectedAtText
              FROM IVT_TRACE_CONSUMPTION_BINDING B
              LEFT JOIN IVT_TRACE_INGESTION_CURSOR C
                ON C.BINDING_ID = B.BINDING_ID
             WHERE B.IS_ACTIVE = 1
            """;
        var rows = await QueryAsync<SqliteLowWatermarkRow>(sql, null, ct);
        if (rows.Count == 0) return null;

        DateTime? lowWatermark = null;
        foreach (var row in rows)
        {
            if (!TryParseCanonicalSqliteUtc(row.EffectiveFromText, out var effectiveFrom))
            {
                throw new InvalidOperationException(
                    $"IVT TRACE retention guard found non-canonical UTC timestamp for active binding "
                    + $"'{row.BindingId}'. Expected yyyy-MM-dd HH:mm:ss[.fffffff] without T/Z/offset.");
            }

            var bindingWatermark = effectiveFrom;
            if (row.CursorBindingId is not null)
            {
                if (!TryParseCanonicalSqliteUtc(row.LastCollectedAtText, out var cursor))
                {
                    throw new InvalidOperationException(
                        $"IVT TRACE retention guard found non-canonical UTC timestamp for active binding "
                        + $"'{row.BindingId}'. Expected yyyy-MM-dd HH:mm:ss[.fffffff] without T/Z/offset.");
                }

                if (cursor > bindingWatermark)
                    bindingWatermark = cursor;
            }

            if (lowWatermark is null || bindingWatermark < lowWatermark.Value)
                lowWatermark = bindingWatermark;
        }

        return lowWatermark;
    }

    private static bool TryParseCanonicalSqliteUtc(string? value, out DateTime parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value)
               && CanonicalSqliteUtcTimestamp.IsMatch(value)
               && DateTime.TryParseExact(
                   value,
                   CanonicalSqliteUtcFormats,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out parsed);
    }

    private sealed class LowWatermarkRow
    {
        public long ActiveBindingCount { get; set; }
        public DateTime? LowWatermark { get; set; }
    }

    private sealed class SqliteLowWatermarkRow
    {
        public string BindingId { get; set; } = string.Empty;
        public string? EffectiveFromText { get; set; }
        public string? CursorBindingId { get; set; }
        public string? LastCollectedAtText { get; set; }
    }
}
