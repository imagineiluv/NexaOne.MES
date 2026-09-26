using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.SYS.Infrastructure;

/// <summary>
/// COM_APPROVAL/COM_APPROVAL_HISTORY에 대한 SYS 소유 범용 승인 저장소입니다.
/// 상태 전이는 Pending→(Approved|Rejected|Cancelled)이고 Rejected/Cancelled 문서는 재요청으로 다시 Pending이 된다.
/// 모든 쓰기는 멱등 키+요청 해시를 이력과 함께 기록해 재시도를 구별하고, 결정/취소는 guarded UPDATE로
/// 동시 전이를 한 명만 이기게 한다.
/// </summary>
public sealed class ApprovalProcess : QueryRepository, IApprovalProcess
{
    private const string ConflictCode = "APPROVAL_REQUEST_CONFLICT";
    private readonly ServiceObjectProcessor _processor;
    private readonly Func<DateTime> _utcNow;

    public ApprovalProcess(EesDataSource dataSource, Func<DateTime>? utcNow = null)
        : base(dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new ServiceObjectProcessor(dataSource);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<string> SubmitAsync(
        ApprovalRequest request,
        string requestedBy,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        var now = _utcNow();

        return await _processor.ExecuteInTransactionAsync(async (conn, txn) =>
        {
            var existing = await CurrentAsync(conn, txn, request.DocKind, request.DocId, ct);
            if (existing is not null)
            {
                if (existing.Status == "Pending")
                {
                    if (existing.RequestHash == requestHash)
                        return existing.ApprovalId;
                    throw new InvalidOperationException(
                        $"{ConflictCode}: a different pending approval already exists for " +
                        $"{request.DocKind}/{request.DocId}.");
                }

                var reopened = await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE COM_APPROVAL SET STATUS = 'Pending', REQUESTED_BY = @by, REQUESTED_AT = @now, " +
                    "DECIDED_BY = NULL, DECIDED_AT = NULL, DECISION_COMMENT = NULL, " +
                    "TITLE = @title, IDEMPOTENCY_KEY = @key, REQUEST_HASH = @hash, " +
                    "UPDATED_BY = @by, UPDATED_AT = @now " +
                    "WHERE APPROVAL_ID = @id AND STATUS IN ('Rejected', 'Cancelled')",
                    new { by = requestedBy, now, title = request.Title, key = idempotencyKey,
                          hash = requestHash, id = existing.ApprovalId },
                    txn, cancellationToken: ct));
                if (reopened != 1)
                    throw new InvalidOperationException(
                        $"{ConflictCode}: approval {existing.ApprovalId} moved while resubmitting.");
                await InsertHistoryAsync(conn, txn, existing.ApprovalId, request.DocKind, request.DocId,
                    existing.Status, "Pending", requestedBy, now, request.Title, idempotencyKey, requestHash, ct);
                return existing.ApprovalId;
            }

            var approvalId = $"APR_{Guid.NewGuid():N}";
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO COM_APPROVAL (APPROVAL_ID, DOC_KIND, DOC_ID, TITLE, STATUS, " +
                    "REQUESTED_BY, REQUESTED_AT, IDEMPOTENCY_KEY, REQUEST_HASH, CREATED_BY, UPDATED_BY) " +
                    "VALUES (@id, @kind, @doc, @title, 'Pending', @by, @now, @key, @hash, @by, @by)",
                    new { id = approvalId, kind = request.DocKind, doc = request.DocId,
                          title = request.Title, by = requestedBy, now, key = idempotencyKey,
                          hash = requestHash },
                    txn, cancellationToken: ct));
            }
            catch (Exception)
            {
                // 동시 제출이 UNIQUE(DOC_KIND,DOC_ID)에 걸린 경우 — 잠긴 뒤 현재 행으로 결과를 결정한다.
                var winner = await CurrentAsync(conn, txn, request.DocKind, request.DocId, ct);
                if (winner?.Status == "Pending" && winner.RequestHash == requestHash)
                    return winner.ApprovalId;
                throw;
            }
            await InsertHistoryAsync(conn, txn, approvalId, request.DocKind, request.DocId,
                "New", "Pending", requestedBy, now, request.Title, idempotencyKey, requestHash, ct);
            return approvalId;
        }, System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
    }

    public async Task DecideAsync(
        ApprovalDecision decision,
        string decidedBy,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct = default)
        => await TransitionAsync(
            decision.ApprovalId,
            toStatus: decision.Approve ? "Approved" : "Rejected",
            actor: decidedBy,
            reason: decision.Comment,
            setDecision: true,
            idempotencyKey, requestHash, ct).ConfigureAwait(false);

    public Task CancelAsync(
        string approvalId,
        string cancelledBy,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct = default)
        => TransitionAsync(
            approvalId,
            toStatus: "Cancelled",
            actor: cancelledBy,
            reason: null,
            setDecision: false,
            idempotencyKey, requestHash, ct);

    public async Task<ApprovalRecord?> GetCurrentAsync(
        string docKind, string docId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ApprovalRow>(
            "SELECT APPROVAL_ID AS ApprovalId, DOC_KIND AS DocKind, DOC_ID AS DocId, " +
            "STATUS AS Status, REQUESTED_BY AS RequestedBy, REQUESTED_AT AS RequestedAt, " +
            "DECIDED_BY AS DecidedBy, DECIDED_AT AS DecidedAt, DECISION_COMMENT AS Comment, " +
            "REQUEST_HASH AS RequestHash " +
            "FROM COM_APPROVAL WHERE DOC_KIND = @kind AND DOC_ID = @doc",
            new { kind = docKind, doc = docId }, ct);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<ApprovalTransition>> GetHistoryAsync(
        string docKind, string docId, CancellationToken ct = default)
    {
        var rows = await QueryAsync<TransitionRow>(
            "SELECT APPROVAL_ID AS ApprovalId, DOC_KIND AS DocKind, DOC_ID AS DocId, " +
            "FROM_STATUS AS FromStatus, TO_STATUS AS ToStatus, CHANGED_BY AS ChangedBy, " +
            "CHANGED_AT AS ChangedAt, REASON AS Reason " +
            "FROM COM_APPROVAL_HISTORY WHERE DOC_KIND = @kind AND DOC_ID = @doc " +
            "ORDER BY CHANGED_AT, HISTORY_ID",
            new { kind = docKind, doc = docId }, ct);
        return rows.Select(static row => new ApprovalTransition(
            row.ApprovalId, row.DocKind, row.DocId, row.FromStatus, row.ToStatus,
            row.ChangedBy, row.ChangedAt, row.Reason)).ToArray();
    }

    public async Task<IReadOnlyList<ApprovalRecord>> ListPendingAsync(
        string? docKind = null, CancellationToken ct = default)
    {
        var rows = await QueryAsync<ApprovalRow>(
            "SELECT APPROVAL_ID AS ApprovalId, DOC_KIND AS DocKind, DOC_ID AS DocId, " +
            "STATUS AS Status, REQUESTED_BY AS RequestedBy, REQUESTED_AT AS RequestedAt, " +
            "DECIDED_BY AS DecidedBy, DECIDED_AT AS DecidedAt, DECISION_COMMENT AS Comment, " +
            "REQUEST_HASH AS RequestHash " +
            "FROM COM_APPROVAL WHERE STATUS = 'Pending' " +
            "AND (@kind IS NULL OR DOC_KIND = @kind) ORDER BY REQUESTED_AT, APPROVAL_ID",
            new { kind = docKind }, ct);
        return rows.Select(static row => row.ToRecord()).ToArray();
    }

    private async Task TransitionAsync(
        string approvalId,
        string toStatus,
        string actor,
        string? reason,
        bool setDecision,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        var now = _utcNow();

        await _processor.ExecuteInTransactionAsync<object?>(async (conn, txn) =>
        {
            var replay = await conn.QueryFirstOrDefaultAsync<ReplayRow>(
                new CommandDefinition(
                    "SELECT APPROVAL_ID AS ApprovalId, TO_STATUS AS ToStatus, REQUEST_HASH AS RequestHash " +
                    "FROM COM_APPROVAL_HISTORY WHERE IDEMPOTENCY_KEY = @key",
                    new { key = idempotencyKey }, txn, cancellationToken: ct));
            if (replay is not null)
            {
                if (replay.RequestHash == requestHash
                    && replay.ApprovalId == approvalId
                    && replay.ToStatus == toStatus)
                    return null;
                throw new InvalidOperationException(
                    $"{ConflictCode}: idempotency key was already used for a different approval transition.");
            }

            var current = await conn.QueryFirstOrDefaultAsync<ApprovalRow>(
                new CommandDefinition(
                    "SELECT APPROVAL_ID AS ApprovalId, DOC_KIND AS DocKind, DOC_ID AS DocId, " +
                    "STATUS AS Status, REQUESTED_BY AS RequestedBy, REQUESTED_AT AS RequestedAt, " +
                    "DECIDED_BY AS DecidedBy, DECIDED_AT AS DecidedAt, DECISION_COMMENT AS Comment, " +
                    "REQUEST_HASH AS RequestHash " +
                    "FROM COM_APPROVAL WHERE APPROVAL_ID = @id",
                    new { id = approvalId }, txn, cancellationToken: ct));
            if (current is null)
                throw new InvalidOperationException($"Approval '{approvalId}' does not exist.");
            if (current.Status != "Pending")
                throw new InvalidOperationException(
                    $"Approval '{approvalId}' is already {current.Status}; only Pending requests can transition.");

            var decisionSql = setDecision
                ? "DECIDED_BY = @actor, DECIDED_AT = @now, DECISION_COMMENT = @reason, "
                : "DECIDED_BY = NULL, DECIDED_AT = NULL, DECISION_COMMENT = NULL, ";
            var moved = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE COM_APPROVAL SET STATUS = @toStatus, " + decisionSql +
                "IDEMPOTENCY_KEY = @key, REQUEST_HASH = @hash, UPDATED_BY = @actor, UPDATED_AT = @now " +
                "WHERE APPROVAL_ID = @id AND STATUS = 'Pending'",
                new { toStatus, actor, now, reason, key = idempotencyKey, hash = requestHash,
                      id = approvalId },
                txn, cancellationToken: ct));
            if (moved != 1)
                throw new InvalidOperationException(
                    $"{ConflictCode}: approval '{approvalId}' transitioned concurrently.");

            await InsertHistoryAsync(conn, txn, approvalId, current.DocKind, current.DocId,
                "Pending", toStatus, actor, now, reason, idempotencyKey, requestHash, ct);
            return null;
        }, System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
    }

    private async Task InsertHistoryAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction txn,
        string approvalId,
        string docKind,
        string docId,
        string fromStatus,
        string toStatus,
        string actor,
        DateTime now,
        string? reason,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO COM_APPROVAL_HISTORY (HISTORY_ID, IDEMPOTENCY_KEY, REQUEST_HASH, " +
            "APPROVAL_ID, DOC_KIND, DOC_ID, FROM_STATUS, TO_STATUS, CHANGED_BY, REASON, CHANGED_AT) " +
            "VALUES (@hid, @key, @hash, @aid, @kind, @doc, @from, @to, @by, @reason, @now)",
            new { hid = $"AH_{Guid.NewGuid():N}", key = idempotencyKey, hash = requestHash,
                  aid = approvalId, kind = docKind, doc = docId, from = fromStatus,
                  to = toStatus, by = actor, reason, now },
            txn, cancellationToken: ct)).ConfigureAwait(false);

    private static async Task<ApprovalRow?> CurrentAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction txn,
        string docKind,
        string docId,
        CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<ApprovalRow>(
            new CommandDefinition(
                "SELECT APPROVAL_ID AS ApprovalId, DOC_KIND AS DocKind, DOC_ID AS DocId, " +
                "STATUS AS Status, REQUESTED_BY AS RequestedBy, REQUESTED_AT AS RequestedAt, " +
                "DECIDED_BY AS DecidedBy, DECIDED_AT AS DecidedAt, DECISION_COMMENT AS Comment, " +
                "REQUEST_HASH AS RequestHash " +
                "FROM COM_APPROVAL WHERE DOC_KIND = @kind AND DOC_ID = @doc",
                new { kind = docKind, doc = docId }, txn, cancellationToken: ct));

    private sealed class ApprovalRow
    {
        public string ApprovalId { get; set; } = string.Empty;
        public string DocKind { get; set; } = string.Empty;
        public string DocId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string RequestedBy { get; set; } = string.Empty;
        public DateTime RequestedAt { get; set; }
        public string? DecidedBy { get; set; }
        public DateTime? DecidedAt { get; set; }
        public string? Comment { get; set; }
        public string RequestHash { get; set; } = string.Empty;

        public ApprovalRecord ToRecord() => new(
            ApprovalId, DocKind, DocId, Status, RequestedBy, RequestedAt, DecidedBy, DecidedAt, Comment);
    }

    private sealed class ReplayRow
    {
        public string ApprovalId { get; set; } = string.Empty;
        public string ToStatus { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
    }

    private sealed class TransitionRow
    {
        public string ApprovalId { get; set; } = string.Empty;
        public string DocKind { get; set; } = string.Empty;
        public string DocId { get; set; } = string.Empty;
        public string FromStatus { get; set; } = string.Empty;
        public string ToStatus { get; set; } = string.Empty;
        public string ChangedBy { get; set; } = string.Empty;
        public DateTime ChangedAt { get; set; }
        public string? Reason { get; set; }
    }
}
