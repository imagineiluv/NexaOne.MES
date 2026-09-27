using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Infrastructure;

/// <summary>Owns the complete parent debit, child creation, two ledger legs, and genealogy commit.</summary>
internal sealed class MaterialLotSplitRepository(EesDataSource dataSource)
    : QueryRepository(dataSource), IMaterialLotSplitStore
{
    private readonly ServiceObjectProcessor _processor = new(dataSource);
    private const string SplitByKeySql = """
        SELECT SPLIT_ID AS SplitId, REQUEST_HASH AS RequestHash,
               PARENT_LOT_ID AS ParentLotId, CHILD_LOT_ID AS ChildLotId,
               QUANTITY AS Quantity, PARENT_BALANCE_BEFORE AS ParentBalanceBefore,
               PARENT_BALANCE_AFTER AS ParentBalanceAfter, PARENT_VERSION AS ParentVersion,
               PARENT_STATUS AS ParentStatus
          FROM IVT_MATERIAL_LOT_SPLIT
         WHERE UPPER(IDEMPOTENCY_KEY)=UPPER(@IdempotencyKey)
        """;

    public async Task<Result<MaterialLotSplitOriginDto>> GetOriginAsync(
        string childLotId, CancellationToken ct)
    {
        const string sql = """
            SELECT SPLIT_ID AS SplitId, PARENT_LOT_ID AS ParentLotId,
                   CHILD_LOT_ID AS ChildLotId, CHILD_LOT_NO AS ChildLotNumber,
                   QUANTITY AS Quantity, PARENT_BALANCE_BEFORE AS ParentBalanceBefore,
                   PARENT_BALANCE_AFTER AS ParentBalanceAfter, PARENT_VERSION AS ParentVersion,
                   PARENT_STATUS AS ParentStatus, PARENT_TX_ID AS ParentTransactionId,
                   CHILD_TX_ID AS ChildTransactionId, OCCURRED_AT AS OccurredAt,
                   ACTOR_ID AS ActorId, SOURCE_SYSTEM AS SourceSystem,
                   SOURCE_EVENT_ID AS SourceEventId
              FROM IVT_MATERIAL_LOT_SPLIT
             WHERE CHILD_LOT_ID=@childLotId
            """;
        var row = await QueryFirstOrDefaultAsync<OriginRow>(sql, new { childLotId }, ct);
        if (row is null)
            return Result.Failure<MaterialLotSplitOriginDto>(Error.NotFound(
                "IVT_SPLIT_ORIGIN_NOT_FOUND", "The LOT has no split origin."));

        return Result.Success(new MaterialLotSplitOriginDto(
            row.SplitId, row.ParentLotId, row.ChildLotId, row.ChildLotNumber,
            ToDecimal(row.Quantity), ToDecimal(row.ParentBalanceBefore),
            ToDecimal(row.ParentBalanceAfter), row.ParentVersion, row.ParentStatus,
            row.ParentTransactionId, row.ChildTransactionId,
            DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc), row.ActorId,
            row.SourceSystem, row.SourceEventId));
    }

    public async Task<Result<MaterialLotSplitDto>> TrySplitAsync(
        NormalizedMaterialLotSplit command, CancellationToken ct)
    {
        try
        {
            return await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
            {
                var existing = await connection.QuerySingleOrDefaultAsync<SplitRow>(
                    new CommandDefinition(SplitByKeySql, command, transaction, cancellationToken: ct));
                if (existing is not null) return Replay(command, existing);

                var identityUsed = await connection.ExecuteScalarAsync<int?>(new CommandDefinition("""
                    SELECT 1 FROM IVT_MATERIAL_LOT_SPLIT
                     WHERE UPPER(SPLIT_ID)=UPPER(@SplitId)
                        OR (UPPER(SOURCE_SYSTEM)=UPPER(@SourceSystem)
                            AND UPPER(SOURCE_EVENT_ID)=UPPER(@SourceEventId))
                    """, command, transaction, cancellationToken: ct));
                if (identityUsed.HasValue) return Conflict("Split ID or source event is already used.");

                var ledgerKeyUsed = await connection.ExecuteScalarAsync<int?>(new CommandDefinition("""
                    SELECT 1 FROM IVT_MATERIAL_TX
                     WHERE UPPER(IDEMPOTENCY_KEY)=UPPER(@IdempotencyKey)
                        OR (UPPER(SOURCE_SYSTEM)=UPPER(@SourceSystem)
                            AND UPPER(SOURCE_EVENT_ID)=UPPER(@SourceEventId))
                    """, command, transaction, cancellationToken: ct));
                if (ledgerKeyUsed.HasValue) return Conflict("Idempotency key or source event is already used.");

                var parent = await connection.QuerySingleOrDefaultAsync<LotRow>(new CommandDefinition("""
                    SELECT MATERIAL_ID AS MaterialId, WAREHOUSE AS Warehouse,
                           CURRENT_QTY AS Balance, UNIT AS Unit,
                           STATUS AS Status, VERSION_NO AS Version, EXPIRY_AT AS ExpiryAt,
                           ACTIVE_FEED_SESSION_ID AS FeedSessionId
                      FROM IVT_MATERIAL_LOT WHERE LOT_ID=@ParentLotId
                    """, command, transaction, cancellationToken: ct));
                if (parent is null)
                    return Result.Failure<MaterialLotSplitDto>(Error.NotFound(
                        "IVT_SPLIT_PARENT_NOT_FOUND", "Parent material LOT was not found."));

                var childExists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM IVT_MATERIAL_LOT WHERE UPPER(LOT_ID)=UPPER(@ChildLotId)",
                    command, transaction, cancellationToken: ct));
                if (childExists.HasValue) return Conflict("Child material LOT ID is already used.");

                var before = ToDecimal(parent.Balance);
                if (parent.Status != "InStock" || parent.Version != command.ExpectedParentVersion
                    || parent.Version == int.MaxValue
                    || parent.FeedSessionId is not null || before < command.Quantity
                    || string.IsNullOrWhiteSpace(parent.MaterialId)
                    || string.IsNullOrWhiteSpace(parent.Unit)
                    || string.IsNullOrWhiteSpace(parent.Warehouse))
                    return Conflict("Parent LOT must be unreserved InStock material with sufficient balance and expected version.");

                var after = before - command.Quantity;
                var parentStatus = after == 0 ? "Consumed" : "InStock";
                var parentVersion = parent.Version + 1;
                var now = DateTime.UtcNow;
                var affected = await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE IVT_MATERIAL_LOT
                       SET CURRENT_QTY=@after, STATUS=@parentStatus, VERSION_NO=@parentVersion,
                           UPDATED_BY=@ActorId, UPDATED_AT=@now
                     WHERE LOT_ID=@ParentLotId AND STATUS='InStock'
                       AND VERSION_NO=@ExpectedParentVersion AND ACTIVE_FEED_SESSION_ID IS NULL
                       AND CAST(COALESCE(CURRENT_QTY,0) AS DECIMAL(38,9)) = CAST(@before AS DECIMAL(38,9))
                    """, new { command.ParentLotId, command.ExpectedParentVersion,
                        command.ActorId, after, parentStatus, parentVersion, now, before },
                    transaction, cancellationToken: ct));
                if (affected == 0) return Conflict("Parent LOT changed concurrently; reload it before splitting.");
                if (affected != 1) throw new DBConcurrencyException($"Split parent update affected {affected} rows.");

                affected = await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO IVT_MATERIAL_LOT
                        (LOT_ID, MATERIAL_ID, LOT_NO, WAREHOUSE, CURRENT_QTY, UNIT, STATUS,
                         RECEIVED_AT, EXPIRY_AT, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES (@ChildLotId, @MaterialId, @ChildLotNumber, @Warehouse, @Quantity, @Unit,
                            'InStock', @OccurredAt, @ExpiryAt, 1, @ActorId, @now, @ActorId, @now)
                    """, new { command.ChildLotId, parent.MaterialId, command.ChildLotNumber,
                        parent.Warehouse, command.Quantity, parent.Unit, command.OccurredAt,
                        parent.ExpiryAt, command.ActorId, now }, transaction, cancellationToken: ct));
                RequireOne(affected, "child LOT insert");

                var parentTxId = Guid.NewGuid().ToString("N");
                var childTxId = Guid.NewGuid().ToString("N");
                affected = await InsertLegAsync(connection, transaction, command,
                    parentTxId, command.ParentLotId, parent.MaterialId!, parent.Warehouse!,
                    "SplitOut", command.IdempotencyKey, command.SourceSystem,
                    command.SourceEventId, before, after, -command.Quantity,
                    command.ExpectedParentVersion, parentVersion, parentStatus, now, ct);
                RequireOne(affected, "parent split ledger insert");
                affected = await InsertLegAsync(connection, transaction, command,
                    childTxId, command.ChildLotId, parent.MaterialId!, parent.Warehouse!,
                    "SplitIn", $"IVT.Split.Child:{command.SplitId}", "IVT.Split.Child",
                    command.SplitId, 0m, command.Quantity, command.Quantity,
                    0, 1, "InStock", now, ct);
                RequireOne(affected, "child split ledger insert");

                affected = await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO IVT_MATERIAL_LOT_SPLIT
                        (SPLIT_ID, IDEMPOTENCY_KEY, SOURCE_SYSTEM, SOURCE_EVENT_ID, REQUEST_HASH,
                         PARENT_LOT_ID, CHILD_LOT_ID, CHILD_LOT_NO, QUANTITY,
                         PARENT_BALANCE_BEFORE, PARENT_BALANCE_AFTER, PARENT_VERSION, PARENT_STATUS,
                         PARENT_TX_ID, CHILD_TX_ID, OCCURRED_AT, ACTOR_ID, CREATED_AT)
                    VALUES (@SplitId, @IdempotencyKey, @SourceSystem, @SourceEventId, @RequestHash,
                            @ParentLotId, @ChildLotId, @ChildLotNumber, @Quantity,
                            @before, @after, @parentVersion, @parentStatus,
                            @parentTxId, @childTxId, @OccurredAt, @ActorId, @now)
                    """, new { command.SplitId, command.IdempotencyKey, command.SourceSystem,
                        command.SourceEventId, command.RequestHash, command.ParentLotId,
                        command.ChildLotId, command.ChildLotNumber, command.Quantity, before, after,
                        parentVersion, parentStatus, parentTxId, childTxId, command.OccurredAt,
                        command.ActorId, now }, transaction, cancellationToken: ct));
                RequireOne(affected, "split genealogy insert");

                return Result.Success(new MaterialLotSplitDto(
                    command.SplitId, command.ParentLotId, command.ChildLotId, command.Quantity,
                    before, after, parentVersion, parentStatus, "InStock", false));
            }, IsolationLevel.Serializable, ct);
        }
        catch (DbException error) when (IsUniqueViolation(error) || IsWriteConflict(error))
        {
            var existing = await QueryFirstOrDefaultAsync<SplitRow>(
                SplitByKeySql, new { command.IdempotencyKey }, ct);
            return existing is null
                ? Conflict(IsWriteConflict(error)
                    ? "LOT split conflicted with a concurrent write; reload before retrying."
                    : "Split ID, child LOT, idempotency key, or source event is already used.")
                : Replay(command, existing);
        }
    }

    private static Task<int> InsertLegAsync(
        DbConnection connection, DbTransaction transaction, NormalizedMaterialLotSplit command,
        string txId, string lotId, string materialId, string warehouse, string operation,
        string idempotencyKey, string sourceSystem, string sourceEventId,
        decimal balanceBefore, decimal balanceAfter, decimal balanceDelta,
        int expectedVersion, int resultVersion, string resultStatus,
        DateTime now, CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO IVT_MATERIAL_TX
                (TX_ID, LOT_ID, MATERIAL_ID, TX_TYPE, QTY, FROM_WAREHOUSE, TO_WAREHOUSE,
                 TX_AT, PROCESSED_BY, STATUS, REMARK, IDEMPOTENCY_KEY, REQUEST_HASH,
                 EXPECTED_VERSION, RESULT_VERSION, SOURCE_SYSTEM, SOURCE_EVENT_ID,
                 CORRELATION_ID, BALANCE_BEFORE, BALANCE_AFTER, BALANCE_DELTA,
                 RESULT_STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@txId, @lotId, @materialId, @operation, @Quantity, @warehouse, @warehouse,
                    @OccurredAt, @ActorId, 'Completed', 'LOT split', @idempotencyKey, @RequestHash,
                    @expectedVersion, @resultVersion, @sourceSystem, @sourceEventId,
                    @SplitId, @balanceBefore, @balanceAfter, @balanceDelta,
                    @resultStatus, @ActorId, @now, @ActorId, @now)
            """, new { txId, lotId, materialId, operation, command.Quantity, warehouse,
                command.OccurredAt, command.ActorId, idempotencyKey, command.RequestHash,
                expectedVersion, resultVersion, sourceSystem, sourceEventId, command.SplitId,
                balanceBefore, balanceAfter, balanceDelta, resultStatus, now },
            transaction, cancellationToken: ct));

    private static Result<MaterialLotSplitDto> Replay(
        NormalizedMaterialLotSplit command, SplitRow existing) =>
        string.Equals(existing.RequestHash, command.RequestHash, StringComparison.Ordinal)
            ? Result.Success(new MaterialLotSplitDto(
                existing.SplitId, existing.ParentLotId, existing.ChildLotId,
                ToDecimal(existing.Quantity), ToDecimal(existing.ParentBalanceBefore),
                ToDecimal(existing.ParentBalanceAfter), existing.ParentVersion,
                existing.ParentStatus, "InStock", true))
            : Conflict("Idempotency key is already used for a different LOT split.");

    private static Result<MaterialLotSplitDto> Conflict(string message) =>
        Result.Failure<MaterialLotSplitDto>(Error.Conflict("IVT_SPLIT_CONFLICT", message));

    private static void RequireOne(int affected, string operation)
    {
        if (affected != 1) throw new DBConcurrencyException($"{operation} affected {affected} rows.");
    }

    private static decimal ToDecimal(object? value) => value is null or DBNull
        ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static bool IsUniqueViolation(DbException error) => error switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                  && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ when error.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
            => error.GetType().GetProperty("Number")?.GetValue(error) is int number
               && number is 2601 or 2627,
        _ => false,
    };

    private static bool IsWriteConflict(DbException error) => error switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6,
        _ when error.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
            => error.GetType().GetProperty("Number")?.GetValue(error) is 1205,
        _ => false,
    };

    private sealed class LotRow
    {
        public string? MaterialId { get; set; }
        public string? Warehouse { get; set; }
        public object? Balance { get; set; }
        public string? Unit { get; set; }
        public string Status { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTime? ExpiryAt { get; set; }
        public string? FeedSessionId { get; set; }
    }

    private sealed class SplitRow
    {
        public string SplitId { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public string ParentLotId { get; set; } = string.Empty;
        public string ChildLotId { get; set; } = string.Empty;
        public object? Quantity { get; set; }
        public object? ParentBalanceBefore { get; set; }
        public object? ParentBalanceAfter { get; set; }
        public int ParentVersion { get; set; }
        public string ParentStatus { get; set; } = string.Empty;
    }

    private sealed class OriginRow
    {
        public string SplitId { get; set; } = string.Empty;
        public string ParentLotId { get; set; } = string.Empty;
        public string ChildLotId { get; set; } = string.Empty;
        public string ChildLotNumber { get; set; } = string.Empty;
        public object? Quantity { get; set; }
        public object? ParentBalanceBefore { get; set; }
        public object? ParentBalanceAfter { get; set; }
        public int ParentVersion { get; set; }
        public string ParentStatus { get; set; } = string.Empty;
        public string ParentTransactionId { get; set; } = string.Empty;
        public string ChildTransactionId { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public string ActorId { get; set; } = string.Empty;
        public string SourceSystem { get; set; } = string.Empty;
        public string SourceEventId { get; set; } = string.Empty;
    }
}
