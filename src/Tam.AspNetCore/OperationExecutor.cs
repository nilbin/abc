using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The execution pipeline (docs/09): authorization → idempotency → structural validation →
/// transaction → handler → extension channel → audit capture → save → response. Every caller
/// — web, MCP, integration, test — comes through here.
/// </summary>
public sealed class OperationExecutor(
    TamModel model,
    IServiceProvider services,
    Func<IServiceProvider, DbContext> dbResolver)
{
    public async Task<OperationResponse> ExecuteAsync(
        string operationId, JsonElement body, OperationContext context, CancellationToken ct)
    {
        if (!model.Operations.TryGetValue(operationId, out var operation))
            return Fail(context, PipelineFindings.UnknownOperation.With(("operation", operationId)));

        // Inactive plugin → the operation does not exist for this tenant (docs/22): checked
        // before authorization so the answer is indistinguishable from an unknown id.
        if (operation.Plugin is { } plugin)
        {
            var active = await ActivationCache.ForAsync(services, dbResolver(services), context.TenantId.Value, ct);
            if (!active.Contains(plugin))
                return Fail(context, PipelineFindings.UnknownOperation.With(("operation", operationId)));
        }

        if (!context.Actor.Can(operation.Permission))
            return Fail(context, PipelineFindings.NotAuthorized.With(("permission", operation.Permission)));

        // Write masking (docs/27 D-A3): an input that carries a sensitive field the actor may not
        // touch is rejected outright — the masked manifest never offered the field, so its presence
        // is either a stale client or a forged request. Checked on the raw body, before binding.
        var maskedWrites = operation.InputFields
            .Where(f => f.IsMaskedFor(context.Actor))
            .Where(f => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(f.WireName, out _))
            .Select(f => PipelineFindings.FieldNotAuthorized
                .With(("field", f.WireName), ("permission", f.SensitivePermission!)).At(f.MemberName))
            .ToList();
        if (maskedWrites.Count > 0)
            return Fail(context, [.. maskedWrites]);

        var db = dbResolver(services);
        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(body.GetRawText())));

        // Idempotency is scoped to the actor as well as tenant+operation: a replay must return the
        // caller's OWN prior outcome, never another actor's stored response (whose output/version
        // may reflect a different ownership scope). Different actor + same key = independent record.
        if (context.IdempotencyKey is { } key)
        {
            var scopedKey = context.Actor.Id + ":" + key;
            var replay = await db.Set<IdempotencyRecord>().FindAsync(
                [context.TenantId.Value, operationId, scopedKey], ct);
            if (replay is not null)
            {
                // Same key + same payload → replay the stored outcome. Different payload → client bug.
                if (replay.PayloadHash != payloadHash)
                    return Fail(context, Finding.Error("pipeline.idempotency-mismatch").Create());
                var stored = JsonSerializer.Deserialize<OperationResponse>(replay.ResponseJson, TamJson.Options)!;
                return stored with
                {
                    Findings = [.. stored.Findings,
                        model.Locales.Resolve(PipelineFindings.IdempotentReplay.Create(), context.Culture)],
                };
            }
        }

        object? input;
        try
        {
            input = body.Deserialize(operation.InputType, TamJson.Options)
                ?? throw new JsonException("null input");
        }
        catch (JsonException)
        {
            return Fail(context, PipelineFindings.InvalidInput.Create());
        }

        var structural = ValidateStructural(operation, input);
        if (structural.Any(f => f.Severity == FindingSeverity.Error))
            return Fail(context, [.. structural]);

        // Tenant automation rules (docs/22 P5): declarative Px conditions over the wire input,
        // firing tenant-authored blocking findings — validation as data, evaluated in-pipeline.
        // (Pure over the input, so safe outside the transaction.)
        var ruleFindings = await RuleEvaluator.EvaluateAsync(
            db, operationId, body, context, model.DefaultCulture, ct);
        if (ruleFindings.Any(f => f.Severity == FindingSeverity.Error))
            return Fail(context, [.. ruleFindings]);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Plugin gates (docs/22 P2): declared preconditions, run only for tenants with the
        // owning plugin active — INSIDE the transaction, so the state a gate checked cannot
        // change underneath the handler it guards. Wire input only, never host CLR types.
        // Operation-specific gates run first, then wildcard gates (docs/28 approvals seam 1):
        // a wildcard gate sees every operation and decides from its own config whether to act.
        var specificGates = model.Gates.GetValueOrDefault(operationId, []);
        var wildcardGates = operationId == GateDefinition.Wildcard
            ? []
            : model.Gates.GetValueOrDefault(GateDefinition.Wildcard, []);
        if (specificGates.Count > 0 || wildcardGates.Count > 0)
        {
            var activePlugins = await ActivationCache.ForAsync(services, db, context.TenantId.Value, ct);
            foreach (var gate in specificGates.Concat(wildcardGates)
                         .Where(g => activePlugins.Contains(g.PluginId)))
            {
                var gateContext = new GateContext(operationId, body, payloadHash, context, services);
                var gateResult = await gate.Handler(gateContext, ct);
                if (!gateResult.IsError) continue;

                // Parking (docs/28 approvals seam 2): the blocking gate may have deferred writes
                // — the envelope it wants to keep. Everything else about this attempt rolls back
                // FIRST; the parked work then runs in a fresh scope pinned to the same tenant, so
                // its commit is independent of the discarded attempt. A parking failure propagates:
                // answering "parked for approval" without a durable envelope would be a lie.
                await transaction.RollbackAsync(ct);
                foreach (var work in gateContext.ParkedWork)
                    await PinnedScope.RunAsync(services, context.TenantId.Value, work, ct);
                return Fail(context, [.. gateResult.Findings]);
            }
        }

        Result result;
        try
        {
            result = await InvokeHandler(operation, input, context, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Fail(context, ConcurrencyFindings.VersionConflict.Create());
        }

        var findings = new List<Finding>(structural.Where(f => f.Severity != FindingSeverity.Error));
        findings.AddRange(ruleFindings.Where(f => f.Severity != FindingSeverity.Error));
        findings.AddRange(result.Findings);
        var conflicts = new List<FieldConflict>(result.Conflicts ?? []);

        // Tenant extension channel: validate + apply against the tracked extensible aggregate.
        if (operation.ExtensibleEntity is { } extensibleType &&
            body.TryGetProperty("extensions", out var extensionsElement) &&
            extensionsElement.ValueKind == JsonValueKind.Object)
        {
            var changes = extensionsElement.Deserialize<Dictionary<string, ExtensionChange>>(TamJson.Options)!;
            if (changes.Count > 0)
            {
                // Deterministic target, never a guess: with ONE tracked instance of the extensible
                // type it is the target; with several (a handler that also loaded sibling rows) the
                // single Added/Modified one is; anything else fails CLOSED — silently writing onto
                // whichever row happened to be tracked first would be cross-record corruption.
                var candidates = db.ChangeTracker.Entries()
                    .Where(e => extensibleType.IsInstanceOfType(e.Entity))
                    .ToList();
                var written = candidates
                    .Where(e => e.State is EntityState.Added or EntityState.Modified)
                    .ToList();
                var tracked = candidates.Count == 1 ? candidates[0]
                    : written.Count == 1 ? written[0]
                    : null;
                if (tracked is null && candidates.Count > 1)
                    findings.Add(PipelineFindings.AmbiguousExtensionTarget.Create());
                if (tracked?.Entity is IExtensible extensible)
                {
                    // The registered registry, not a bare EF one: plugin-packaged fields
                    // (docs/22 P2) must validate exactly like tenant-defined fields.
                    var registry = services.GetService(typeof(IExtensionRegistry)) as IExtensionRegistry
                        ?? new EfExtensionRegistry(db);
                    var specs = await registry.For(
                        context.TenantId, TamModel.EntityKey(extensibleType), ct);
                    var applied = ExtensionApplier.Apply(
                        extensible, tracked.State == EntityState.Added, changes, specs);
                    findings.AddRange(applied.Findings);
                    conflicts.AddRange(applied.Conflicts);
                }
            }
        }

        var blocked = result.IsError
            || conflicts.Count > 0
            || findings.Any(f => f is { Severity: FindingSeverity.Error, BlocksSubmission: true });

        if (blocked)
        {
            await transaction.RollbackAsync(ct);
            return new OperationResponse(
                null,
                Resolve(findings, context),
                [],
                null,
                null,
                conflicts.Count > 0 ? conflicts : null);
        }

        // Outbox (docs/09): explicit event effects persist in this same transaction —
        // the event exists if and only if the operation committed.
        foreach (var effect in result.Effects.OfType<EventPublished>())
        {
            db.Add(new OutboxRecord
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                OperationId = operationId,
                EventType = effect.Event,
                PayloadJson = JsonSerializer.Serialize(effect.Payload, TamJson.Options),
                CreatedAtIso = IsoTime.Now(),
            });
        }

        var audit = TamAudit.Capture(db, context, operationId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (ct.IsCancellationRequested is false)
        {
            // A check-then-insert race lost to a unique index (two concurrent installs,
            // activations, definitions). The serial path would have produced a duplicate
            // finding; surface the same conflict shape instead of a 500.
            await transaction.RollbackAsync(ct);
            return Fail(context, ConcurrencyFindings.VersionConflict.Create());
        }

        var effects = result.Effects.Concat(TamAudit.InferEffects(audit)).ToList();
        var version = db.ChangeTracker.Entries()
            .Select(e => e.Entity).OfType<IVersioned>()
            .Select(v => (long?)v.Version).FirstOrDefault();

        var response = new OperationResponse(
            Output(result),
            Resolve(findings, context),
            effects.Select(e => (object)e).ToList(),
            version,
            audit.Id.ToString("N"),
            null);

        if (context.IdempotencyKey is { } storeKey)
        {
            var scopedStoreKey = context.Actor.Id + ":" + storeKey;
            db.Add(new IdempotencyRecord
            {
                Key = scopedStoreKey,
                TenantId = context.TenantId.Value,
                OperationId = operationId,
                PayloadHash = payloadHash,
                ResponseJson = JsonSerializer.Serialize(response, TamJson.Options),
                Timestamp = DateTimeOffset.UtcNow,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Concurrent identical request won the unique-key race: this attempt rolls
                // back entirely and the stored outcome is replayed (or rejected on mismatch).
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                var winner = await db.Set<IdempotencyRecord>().FindAsync(
                    [context.TenantId.Value, operationId, scopedStoreKey], ct);
                if (winner is null || winner.PayloadHash != payloadHash)
                    return Fail(context, Finding.Error("pipeline.idempotency-mismatch").Create());
                var stored = JsonSerializer.Deserialize<OperationResponse>(winner.ResponseJson, TamJson.Options)!;
                return stored with
                {
                    Findings = [.. stored.Findings,
                        model.Locales.Resolve(PipelineFindings.IdempotentReplay.Create(), context.Culture)],
                };
            }
        }

        await transaction.CommitAsync(ct);

        // Live-refresh fan-out through the backplane: local subscribers immediately, and every other
        // instance via the configured transport (Postgres NOTIFY). Carries the entity effects grids
        // refresh on — those exist only here, not in the event outbox.
        (services.GetService(typeof(IEffectBackplane)) as IEffectBackplane)
            ?.Send(context.TenantId.Value, operationId, response.Effects);

        return response;
    }

    /// <summary>Structural validation from the model: requiredness by nullability, semantic type rules.</summary>
    private static List<Finding> ValidateStructural(OperationDefinition operation, object input)
    {
        var findings = new List<Finding>();
        foreach (var field in operation.InputFields)
        {
            var property = operation.InputType.GetProperty(field.MemberName)!;
            var value = property.GetValue(input);

            if (field.Required && IsEmpty(value))
            {
                findings.Add(ValidationFindings.Required.At(field.WireName));
                continue;
            }

            var toValidate = field.IsChangeSet && value is not null
                ? value.GetType().GetProperty("Value")!.GetValue(value)
                : value;
            if (toValidate is null) continue;

            var normalized = field.Semantic.Normalize(ValueWrapper.Unwrap(toValidate));
            if (field.Semantic.Validate(normalized) is { } invalid)
                findings.Add(invalid.At(field.WireName));
        }
        return findings;
    }

    private static bool IsEmpty(object? value)
    {
        var unwrapped = ValueWrapper.Unwrap(value);
        return unwrapped switch
        {
            null => true,
            string s => s.Length == 0,
            Guid g => g == Guid.Empty,
            _ => false,
        };
    }

    /// <summary>Generator-wired parameter injection (docs/review-notes): input, context, DI services, ct.</summary>
    internal async Task<Result> InvokeHandler(
        OperationDefinition operation, object input, OperationContext context, CancellationToken ct)
    {
        var args = BindParameters(operation.Execute, input, context, ct);
        var invocation = operation.Execute.Invoke(null, args)
            ?? throw new InvalidOperationException(
                $"TAM004: {operation.Id} Execute returned null — expected Task<Result<...>>.");
        var task = (Task)invocation;
        await task;
        var resultProperty = task.GetType().GetProperty("Result")
            ?? throw new InvalidOperationException(
                $"TAM004: {operation.Id} Execute must return Task<Result> or Task<Result<T>>, not a plain Task.");
        return (Result)resultProperty.GetValue(task)!;
    }

    internal object?[] BindParameters(
        MethodInfo method, object? input, OperationContext context, CancellationToken ct)
    {
        return method.GetParameters().Select(p =>
        {
            if (p.ParameterType == typeof(CancellationToken)) return (object?)ct;
            if (p.ParameterType == typeof(OperationContext)) return context;
            if (input is not null && p.ParameterType.IsInstanceOfType(input) && p.Position == 0) return input;
            if (p.ParameterType == typeof(DerivationContext)) return new DerivationContext(context);
            return services.GetService(p.ParameterType)
                ?? throw new InvalidOperationException(
                    $"DI001: cannot bind parameter '{p.Name}' ({p.ParameterType.Name}) of {method.DeclaringType?.Name}.{method.Name}.");
        }).ToArray();
    }

    private static object? Output(Result result) =>
        result.GetType() is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(Result<>)
            ? t.GetProperty("Output")!.GetValue(result)
            : null;

    private OperationResponse Fail(OperationContext context, params Finding[] findings) =>
        new(null, Resolve(findings, context), [], null, null);

    private IReadOnlyList<Finding> Resolve(IEnumerable<Finding> findings, OperationContext context) =>
        findings.Select(f => model.Locales.Resolve(f, context.Culture)).ToList();
}

/// <summary>Context available to server derivations; services arrive via parameter injection.</summary>
public sealed record DerivationContext(OperationContext Operation);
