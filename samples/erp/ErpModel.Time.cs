using Erp.Features;
using Tam;

namespace Erp;

public static partial class ErpModel
{
    // Time keeps a page for the technician's field mode (bookings are history; the only
    // state change is time.approve, riding the grid as a row action). Time reads from the
    // OFFICE's angle inside the order record's tabs.
    private static TamModelBuilder AddTime(this TamModelBuilder model) => model
        .Page("time", page => page
            .Grid("web.time.list")
            .Record(record => record
                .Detail("time.detail", key: "timeEntryId")
                .Title("orderNumber")))

        // Booking rides the orders grid as a row action, so the order arrives prefilled
        // (hidden here); rate and amount are filled live by the time.book derivations —
        // RecomputeIfUntouched keeps them tracking until the user overrides the rate.
        .Form<BookTime.Input>("web.time.book", "time.book", form =>
        {
            form.Field(x => x.OrderId).Renderer("hidden");
            form.Field(x => x.Date);
            form.Field(x => x.Hours);
            form.Field(x => x.HourlyRate)
                .OnSourceChange(DependentValuePolicy.RecomputeIfUntouched);
            form.Field(x => x.Amount).ReadOnly()   // computed display seat (docs/34 M5)
                .OnSourceChange(DependentValuePolicy.RecomputeIfUntouched);
            form.Field(x => x.Note);
        })

        // Columns by convention (D-P6) — configure only declares the actions.
        .Grid<TimeEntryList.Result>("web.time.list", "time.list", grid =>
        {
            grid.RowAction("time.approve");
        });
}
