namespace Tam;

/// <summary>Describes what happened during an operation. Persistence effects are inferred; others are explicit.</summary>
public abstract record OperationEffect(string Type);

public sealed record EntityCreated(string Entity, string Id) : OperationEffect("entity-created");

public sealed record EntityModified(string Entity, string Id, IReadOnlyList<string> Fields)
    : OperationEffect("entity-modified");

public sealed record EventPublished(string Event, object Payload) : OperationEffect("event-published")
{
    /// <summary>The typed publish (docs/31 "events are records"): the id comes from the
    /// payload record's [DomainEvent] attribute, so a publish site cannot name one event and
    /// ship another's shape. TAM009 steers anonymous-object payloads here; the explicit
    /// (event, payload) form stays for genuinely dynamic publishers (tenant automation rules).</summary>
    public EventPublished(object payload) : this(DomainEventId(payload), payload) { }

    private static string DomainEventId(object payload) =>
        (payload.GetType().GetCustomAttributes(typeof(DomainEventAttribute), false)
            .Cast<DomainEventAttribute>().FirstOrDefault()
         ?? throw new InvalidOperationException(
             $"'{payload.GetType().Name}' is not a [DomainEvent] record — declare it, or use "
             + "the explicit (event, payload) form for genuinely dynamic publishing.")).Id;
}

public class Result
{
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    public IReadOnlyList<OperationEffect> Effects { get; init; } = [];

    /// <summary>Structured field conflicts from the three-way merge, when the failure is concurrency.</summary>
    public IReadOnlyList<FieldConflict>? Conflicts { get; init; }

    public bool IsError => Findings.Any(f => f.Severity == FindingSeverity.Error);

    public static Result Success() => new();

    public static Result Fail(Finding finding) => new() { Findings = [finding] };

    public Result<T> As<T>() => new() { Findings = Findings, Effects = Effects };

    public static implicit operator Result(Finding finding) => Fail(finding);

    public static implicit operator Result(FindingFactory factory) => Fail(factory.Create());
}

public sealed class Result<T> : Result
{
    public T? Output { get; init; }

    public Result<T> Effect(OperationEffect effect) => new()
    {
        Output = Output, Findings = Findings, Effects = [.. Effects, effect],
    };

    public Result<T> Warn(Finding finding) => new()
    {
        Output = Output, Findings = [.. Findings, finding], Effects = Effects,
    };

    public static implicit operator Result<T>(T output) => new() { Output = output };

    public static implicit operator Result<T>(Finding finding) => new() { Findings = [finding] };

    public static implicit operator Result<T>(FindingFactory factory) =>
        new() { Findings = [factory.Create()] };
}
