# NexaOne MES — Common Execution State Machine Backlog

> Status: **Design backlog / not implemented**
>
> Purpose: preserve a future improvement candidate discovered while reviewing public MES implementations. This document does **not** authorize replacing the current POM state models.

## 1. Why this is a backlog item

NexaOne MES already owns domain-specific lifecycle rules:

- `PomWorkOrderStatus`: `Created -> Released -> Started -> Completed`, plus `Cancelled`.
- `PomWorkOrder` owns Release/Start/ReportProduction/Complete/Hold/ReleaseHold/Cancel invariants.
- `LotStateMachine` explicitly controls `Created / Queued / Processing / Completed / Consumed`.
- LOT TrackIn/TrackOut, routing, Hold, Rework and optimistic concurrency/idempotency already belong to the POM domain.

Therefore the target is **not** to add another generic state machine on top of these models immediately.

The candidate improvement is to determine whether multiple executable domains eventually need a small shared **execution lifecycle contract** while preserving each aggregate's own business state and invariants.

## 2. Candidate common lifecycle

A future common execution vocabulary may be:

```text
Created
  -> Released
  -> Ready
  -> Running
       <-> Held
       -> Suspended
  -> PartiallyCompleted
  -> Completed
  -> Closed

Terminal/exception paths:
  -> Cancelled
  -> Failed
```

These names are a **semantic interoperability model**, not a replacement enum for `PomWorkOrderStatus` or `LotState`.

## 3. Mapping concept

Example only:

| Domain state | Common execution semantic |
|---|---|
| WorkOrder.Created | Created |
| WorkOrder.Released | Released / Ready depending on admission |
| WorkOrder.Started | Running |
| WorkOrder.IsHold=true | Held overlay |
| WorkOrder.Completed | Completed |
| WorkOrder.Cancelled | Cancelled |
| Lot.Queued | Ready |
| Lot.Processing + Run | Running |
| Lot.IsHold=true | Held overlay |
| Lot.Completed | Completed |
| Lot.Consumed | Domain terminal state; do not force-map to Completed without a consumer requirement |

The mapping must remain explicit and lossless enough that consumers never infer MES business rules from the generic lifecycle alone.

## 4. Recommended architecture if implemented later

Keep domain state authoritative:

```text
POM WorkOrder / Lot / future Batch / Campaign / Maintenance
                    |
                    | explicit adapter/projection
                    v
          Common Execution Lifecycle
                    |
          observability / UI / orchestration
```

Possible contracts:

```csharp
public enum ExecutionPhase
{
    Created,
    Released,
    Ready,
    Running,
    Held,
    Suspended,
    PartiallyCompleted,
    Completed,
    Closed,
    Cancelled,
    Failed
}

public interface IExecutionLifecycleView
{
    string ExecutionId { get; }
    string ExecutionType { get; }
    ExecutionPhase Phase { get; }
    bool IsTerminal { get; }
}
```

Do **not** put production quantity, routing, LOT genealogy, QMS, recipe, equipment admission or rework rules into this common contract.

## 5. What must stay domain-specific

The following remain inside POM/owning modules:

- WorkOrder Release/Start/Complete/Cancel rules.
- LOT TrackIn/TrackOut and `LotStateMachine`.
- Hold eligibility and domain-specific resume behavior.
- routing predecessor checks and route deviations.
- rework/bypass/alternative process rules.
- production quantity and scrap invariants.
- LOT/QMS completion policies.
- optimistic concurrency and idempotency boundaries.
- transactional outbox/domain events.

A common lifecycle must never allow a caller to bypass these aggregate methods.

## 6. Primary use cases that could justify the abstraction

Implement only when at least two or more real domains need the same cross-domain behavior:

1. unified operations dashboard for WO/Batch/Campaign/Maintenance/Inspection;
2. common execution timeline/audit projection;
3. common Hold/Resume presentation while retaining domain-specific commands;
4. generic orchestration/agent tooling that needs lifecycle visibility without knowing every MES aggregate;
5. shared metrics such as ready/running/held/completed counts.

If those consumers do not materialize, keep the current domain-specific state models.

## 7. Evaluation checklist before implementation

- Inventory all status enums and transition methods in POM, EMS, QMS and future Batch/Campaign modules.
- Identify semantic collisions: `Released` vs `Ready`, `Completed` vs `Closed`, Hold as state vs orthogonal flag.
- Confirm whether Hold should remain an orthogonal overlay rather than a primary state.
- Define terminal-state semantics without collapsing `Consumed`, `Cancelled`, `Failed` and `Completed`.
- Ensure mapping is one-way/read-oriented unless a concrete command abstraction is justified.
- Verify no new generic API can mutate aggregates directly.
- Add mapping contract tests and transition regression tests.
- Preserve existing DB status values and API compatibility unless a separate migration is approved.

## 8. Suggested implementation order

1. **Inventory only** — document existing lifecycle models.
2. **Read model** — introduce an execution lifecycle projection/adapter.
3. **UI/observability consumer** — prove that the abstraction removes duplication.
4. **Optional common contracts** — only after at least two domains use them.
5. **Never centralize domain transition rules merely for uniformity.**

## 9. Decision guardrail

The default decision is **do not refactor the existing POM state machines yet**.

Proceed only when the shared lifecycle solves a demonstrated cross-domain requirement. NexaOne MES already has strong aggregate-owned transitions; the improvement opportunity is a common semantic projection above them, not replacement of working domain state machines.
