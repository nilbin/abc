using Tam;

namespace Erp;

// The Time aggregate: value types, finding factories, entity (docs/02).

public readonly record struct TimeEntryId(Guid Value);


[Multiline, MaxLength(500)]
public readonly record struct TimeNote(string Value);


public enum TimeEntryStatus { Draft, Approved }


public static class TimeFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("time.not-found");
    public static readonly FindingFactory InvalidHours = Finding.Error("time.invalid-hours");
    public static readonly FindingFactory InvalidRate = Finding.Error("time.invalid-rate");
    public static readonly FindingFactory AlreadyApproved = Finding.Error("time.already-approved");
    public static readonly FindingFactory WorkOrderClosed = Finding.Error("time.work-order-closed");
}


/// <summary>A technician's time booking on a work order (docs/34 M3). Owned by the BOOKING
/// technician — the paired-atom OWN scope rides TechnicianActorId. Amount is computed and
/// STORED at booking time (hours × rate), so later rate conventions never rewrite history;
/// approval is an intent operation (EDIT001), never an edit.</summary>
public sealed class TimeEntry : Tam.EntityFrameworkCore.ITenantScoped
{
    private TimeEntry() { }

    public TimeEntryId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public WorkOrderId WorkOrderId { get; private set; }
    public string TechnicianActorId { get; private set; } = "";
    // Snapshot of the technician's display name at booking time — same denormalization as
    // WorkOrder.AssignedToName (docs/34 friction log: no actor-reference rendering story).
    public string TechnicianName { get; private set; } = "";
    public DateOnly Date { get; private set; }
    public decimal Hours { get; private set; }
    public Money HourlyRate { get; private set; }
    public Money Amount { get; private set; }
    public TimeNote? Note { get; private set; }
    public TimeEntryStatus Status { get; private set; }

    public static TimeEntry Book(
        string tenantId, WorkOrderId workOrderId, string technicianActorId, string technicianName,
        DateOnly date, decimal hours, decimal hourlyRate, TimeNote? note) => new()
    {
        Id = new TimeEntryId(Guid.NewGuid()),
        TenantId = tenantId,
        WorkOrderId = workOrderId,
        TechnicianActorId = technicianActorId,
        TechnicianName = technicianName,
        Date = date,
        Hours = hours,
        HourlyRate = hourlyRate,
        Amount = decimal.Round(hours * hourlyRate, 2),
        Note = note,
        Status = TimeEntryStatus.Draft,
    };

    public Result Approve()
    {
        if (Status == TimeEntryStatus.Approved) return TimeFindings.AlreadyApproved;
        Status = TimeEntryStatus.Approved;
        return Result.Success();
    }
}
