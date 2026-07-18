using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

public static class TimeRules
{
    /// <summary>The display name to snapshot onto the entry — the BOOKING actor's own account
    /// name, resolved once at write time (same denormalization as Order.AssignedToName;
    /// docs/34 friction log: views have no actor-reference rendering story).</summary>
    public static async Task<string> TechnicianDisplayName(
        string actorId, ErpDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(actorId, out var accountId)) return actorId;
        var name = await db.Set<AccountEntity>()
            .Where(a => a.Id == accountId)
            .Select(a => a.DisplayName)
            .SingleOrDefaultAsync(ct);
        return name ?? actorId;
    }
}

/// <summary>Booking always books the ACTOR's own time — ownership is established at creation,
/// not chosen in the form. Amount is server-computed (hours × rate); the form's Amount field
/// is a live preview seat filled by the derivation below, never trusted here.</summary>
[Operation("time.book")]
[Authorize("time.book")]
public static class BookTime
{
    public sealed record Input(
        [property: LabelKey("labels.order")] OrderId OrderId,
        DateOnly Date,
        decimal Hours,
        Money HourlyRate,
        TimeNote? Note = null,
        Money? Amount = null);

    public sealed record Output(TimeEntryId TimeEntryId, Money Amount);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;
        // Time books onto LIVE work: a completed or cancelled order takes no new hours.
        if (order.Status is OrderStatus.Completed or OrderStatus.Cancelled)
            return TimeFindings.OrderClosed.At(nameof(Input.OrderId));

        if (input.Hours <= 0 || input.Hours > 24)
            return TimeFindings.InvalidHours.At(nameof(Input.Hours));
        if (input.HourlyRate < 0)
            return TimeFindings.InvalidRate.At(nameof(Input.HourlyRate));

        var technicianName = await TimeRules.TechnicianDisplayName(context.Actor.Id, db, ct);
        var entry = TimeEntry.Book(
            context.TenantId.Value, order.Id, context.Actor.Id, technicianName,
            input.Date, input.Hours, input.HourlyRate, input.Note);
        db.TimeEntries.Add(entry);
        return new Output(entry.Id, entry.Amount);
    }
}

public static class BookTimeDerivations
{
    /// <summary>Rate default: the technician's most recent booked rate, else the catalog's
    /// hour-priced labor item (the seeded "Servicetekniker, timme"). Keyed on OrderId
    /// because the row action prefills it when the form opens — the "SOMETHING must
    /// fire" trigger choice (docs/34 friction log).</summary>
    [ServerDerivation("time.book.rate-default")]
    [DependsOn(nameof(BookTime.Input.OrderId))]
    public static async Task<DerivationResult> RateDefault(
        BookTime.Input input, DerivationContext context, ErpDbContext db, CancellationToken ct)
    {
        if (input.OrderId.Value == Guid.Empty) return DerivationResult.Empty;

        var actorId = context.Operation.Actor.Id;
        var lastRate = await db.TimeEntries
            .Where(x => x.TechnicianActorId == actorId)
            .OrderByDescending(x => x.Date)
            .Select(x => (decimal?)x.HourlyRate)
            .FirstOrDefaultAsync(ct);
        var rate = lastRate ?? await db.Stock
            .Where(x => x.IsActive && x.Unit == StockUnit.Hour)
            .OrderBy(x => x.Name)
            .Select(x => (decimal?)x.UnitPrice)
            .FirstOrDefaultAsync(ct);
        if (rate is null) return DerivationResult.Empty;

        return DerivationResult.Empty.Suggest(nameof(BookTime.Input.HourlyRate), rate.Value);
    }

    /// <summary>The live line amount: hours × rate, recomputed as either changes. The value is
    /// a form-side preview only — the operation recomputes authoritatively at write time.</summary>
    [ServerDerivation("time.book.amount")]
    [DependsOn(nameof(BookTime.Input.Hours), nameof(BookTime.Input.HourlyRate))]
    public static Task<DerivationResult> Amount(
        BookTime.Input input, DerivationContext context, CancellationToken ct)
    {
        if (input.Hours <= 0 || input.HourlyRate < 0)
            return Task.FromResult(DerivationResult.Empty);
        return Task.FromResult(DerivationResult.Empty.Suggest(
            nameof(BookTime.Input.Amount), decimal.Round(input.Hours * input.HourlyRate, 2)));
    }
}

/// <summary>Approval is an INTENT (EDIT001): Draft → Approved is consequential state — the
/// M4 invoicing seam drafts from APPROVED time only — so it never rides a Change&lt;T&gt;.
/// Dispatcher-level: the atom is granted to the office, not paired with an own scope.</summary>
[Operation("time.approve")]
[Authorize("time.approve")]
public static class ApproveTime
{
    public sealed record Input([property: LabelKey("labels.time-entry")] TimeEntryId TimeEntryId);

    public sealed record Output(TimeEntryStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var entry = await db.TimeEntries.SingleOrDefaultAsync(x => x.Id == input.TimeEntryId, ct);
        if (entry is null) return TimeFindings.NotFound.Create();

        var result = entry.Approve();
        if (result.IsError) return result.As<Output>();
        return new Output(entry.Status);
    }
}

[View("time.list")]
[Authorize("time.read")]
[Widens("time.read-all")]
public static class TimeEntryList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public TimeEntryId Id { get; init; }
        public OrderNumber OrderNumber { get; init; }
        public DateOnly Date { get; init; }
        [LabelKey("labels.technician")]
        public string TechnicianName { get; init; } = "";
        public decimal Hours { get; init; }
        public Money HourlyRate { get; init; }
        public Money Amount { get; init; }
        public TimeEntryStatus Status { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        // The technician's own time unless time.read-all widens (the office sees the board).
        // Both join sides ride the ambient tenant filter — nothing here widens, so no InScope.
        var rows = db.TimeEntries
            .ScopedUnless(context, "time.read-all", x => x.TechnicianActorId)
            .Join(db.Orders, t => t.OrderId, o => o.Id, (t, o) => new Result
            {
                Id = t.Id, OrderNumber = o.Number, Date = t.Date,
                TechnicianName = t.TechnicianName, Hours = t.Hours,
                HourlyRate = t.HourlyRate, Amount = t.Amount, Status = t.Status,
            });
        if (!string.IsNullOrWhiteSpace(query.Search))
            rows = rows.Where(x =>
                ((string)(object)x.OrderNumber).Contains(query.Search!) ||
                x.TechnicianName.Contains(query.Search!));
        return rows;
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Date), nameof(Result.OrderNumber), nameof(Result.Hours),
            nameof(Result.Amount))
        .Filterable(nameof(Result.Status), nameof(Result.Date), nameof(Result.TechnicianName),
            nameof(Result.OrderNumber))
        .DefaultSort(nameof(Result.Date), descending: true);
}

/// <summary>The record surface behind the declared time page — read-only (docs/32: a record
/// with no form): the only state change is time.approve, an intent.</summary>
[View("time.detail")]
[Authorize("time.read")]
[Widens("time.read-all")]
public static class TimeEntryDetail
{
    public sealed record Query(TimeEntryId TimeEntryId);

    public sealed record Result
    {
        public TimeEntryId Id { get; init; }
        public OrderNumber OrderNumber { get; init; }
        public DateOnly Date { get; init; }
        [LabelKey("labels.technician")]
        public string TechnicianName { get; init; } = "";
        public decimal Hours { get; init; }
        public Money HourlyRate { get; init; }
        public Money Amount { get; init; }
        public TimeNote? Note { get; init; }
        public TimeEntryStatus Status { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context) =>
        db.TimeEntries.Where(x => x.Id == query.TimeEntryId)
            .ScopedUnless(context, "time.read-all", x => x.TechnicianActorId)
            .Join(db.Orders, t => t.OrderId, o => o.Id, (t, o) => new Result
            {
                Id = t.Id, OrderNumber = o.Number, Date = t.Date,
                TechnicianName = t.TechnicianName, Hours = t.Hours,
                HourlyRate = t.HourlyRate, Amount = t.Amount, Note = t.Note, Status = t.Status,
            });
}
