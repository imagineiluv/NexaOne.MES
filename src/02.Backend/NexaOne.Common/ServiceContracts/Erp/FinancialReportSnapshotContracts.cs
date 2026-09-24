using NexaFramework.Service.Erp;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>Persisted report families. Their date axes remain defined by the Framework report contracts.</summary>
public enum FinancialReportSnapshotKind
{
    Financial = 0,
    CashFlow = 1
}

/// <summary>Append-only history actions for one persisted report snapshot.</summary>
public enum FinancialReportSnapshotAuditAction
{
    Created = 0,
    Regenerated = 1,
    Downloaded = 2
}

/// <summary>Stable metadata for a persisted report snapshot.</summary>
public sealed record FinancialReportSnapshotSummary(
    Guid Id,
    FinancialReportSnapshotKind Kind,
    DateOnly Start,
    DateOnly End,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string ContentHash);

/// <summary>Persisted typed report payload. Exactly one payload matches <see cref="Summary"/> kind.</summary>
public sealed record FinancialReportSnapshot(
    FinancialReportSnapshotSummary Summary,
    FinancialReport? FinancialReport,
    CashFlowReport? CashFlowReport);

/// <summary>Result of comparing a fresh atomic report with its persisted snapshot.</summary>
public sealed record FinancialReportSnapshotComparison(
    Guid SnapshotId,
    bool Matches,
    string StoredContentHash,
    string CurrentContentHash,
    DateTimeOffset ComparedAt);

/// <summary>Stored CSV download with a server-owned safe file name.</summary>
public sealed record FinancialReportSnapshotDownload(string FileName, string Content);

/// <summary>One append-only snapshot history event.</summary>
public sealed record FinancialReportSnapshotAuditEntry(
    Guid Id,
    Guid SnapshotId,
    FinancialReportSnapshotAuditAction Action,
    string UserId,
    DateTimeOffset At,
    string ObservedContentHash,
    bool? Matches);
