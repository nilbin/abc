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

        if (!context.Actor.Can(operation.Permission))
            return Fail(context, PipelineFindings.NotAuthorized.With(("permission", operation.Permission)));

        var db = dbResolver(services);
        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(body.GetRawText())));

        if (context.IdempotencyKey is { } key)
        {
            var replay = await db.Set<IdempotencyRecord>().FindAsync(
                [context.TenantId.Value, operationId, key], ct);
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

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

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
                var tracked = db.ChangeTracker.Entries()
                    .FirstOrDefault(e => extensibleType.IsInstanceOfType(e.Entity));
                if (tracked?.Entity is IExtensible extensible)
                {
                    var registry = new EfExtensionRegistry(db);
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
                CreatedAtIso = DateTimeOffset.UtcNow.ToString("O"),
            });
        }

        var audit = TamAudit.Capture(db, context, operationId);
        await db.SaveChangesAsync(ct);

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
            db.Add(new IdempotencyRecord
            {
                Key = storeKey,
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
                    [context.TenantId.Value, operationId, storeKey], ct);
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

        (services.GetService(typeof(EffectBroadcaster)) as EffectBroadcaster)
            ?.Publish(context.TenantId.Value, operationId, response.Effects);

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
