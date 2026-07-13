# 09 — Execution Envelope and Effects

## Execution envelope

All callers should invoke operations through the same envelope.

```csharp
public sealed record OperationRequest<TInput>(
    TInput Input,
    Actor Actor,
    TenantId TenantId,
    InvocationSource Source,
    string? IdempotencyKey,
    string? CorrelationId,
    long? ExpectedVersion);
```

Invocation sources:

```
Web
Admin
Mobile
MCP
Integration
Workflow
ScheduledJob
Internal
```

Result:

```csharp
public sealed record OperationResult<TOutput>(
    TOutput? Output,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<OperationEffect> Effects,
    long? NewVersion,
    string? AuditReference);
```

## Pipeline responsibilities

The execution pipeline should handle:

- Authorization
- Structural validation
- Derivation or preflight validation where needed
- Transaction creation
- Operation execution
- Optimistic concurrency
- Outbox persistence
- Audit
- Idempotency
- Correlation
- Observability
- Error conversion

For extensible operations the pipeline additionally validates and applies tenant extension changes inside the same transaction — before/alongside the handler, never as a separate commit ([15-extensibility.md](15-extensibility.md)).

## Effects

Effects describe **what happened**.

Examples:

```
Order created
Order description changed
Order completed
OrderCreated event published
Invoice generation requested
Notification requested
```

Many persistence effects can be inferred from EF Core change tracking. Non-persistence effects should be explicit:

```csharp
return result
    .Effect(new NotificationRequested(...))
    .Effect(new DocumentGenerationRequested(...));
```

Effects can drive:

- Audit
- Cache invalidation
- Frontend refresh
- Integration dispatch
- Agent explanations
- Tests
- Operational diagnostics
