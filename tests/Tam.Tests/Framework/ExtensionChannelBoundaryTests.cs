using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// The tenant extension channel is parsed + validated at the REQUEST BOUNDARY (Sol re-review round 13,
/// F3), the same place compiled input is — not read raw after the handler has run inside the transaction.
/// One coherent contract: extensions on a non-extensible operation, a non-object channel, an entry that
/// is not a Change object, or a null entry all answer a structured pipeline.invalid-input, and the
/// handler never runs. Verified through a FRAMEWORK-owned extensible entity, independent of any sample.
/// </summary>
public sealed class ExtensionChannelBoundaryTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;

    public async Task InitializeAsync() =>
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());

    public async Task DisposeAsync() => await host.DisposeAsync();

    private Task<int> WidgetCount() => host.QueryDbAsync("demo", db => db.Widgets.CountAsync());

    private static object WidgetBody(object extensions) => new { name = "W", extensions };

    // A raw-JSON body: the typed test serializer drops CLR nulls, but a real wire client sends an explicit
    // JSON null — so `extensions: null` / `{ "note": null }` must be built as raw JSON to survive.
    private static JsonElement RawWidgetBody(string extensionsJson) => JsonSerializer.Deserialize<JsonElement>(
        $$"""{"name":"W","extensions":{{extensionsJson}}}""");

    [Fact]
    public async Task Extensions_on_a_non_extensible_operation_are_rejected_and_the_handler_does_not_run()
    {
        // widgets.create-plain declares no extensible entity — a caller believing an extension was accepted
        // must be told otherwise, and the row must not be created.
        var actor = host.Actor("demo", "widgets.create");
        (await actor.ExecuteAsync("widgets.create-plain", new
        {
            name = "W",
            extensions = new { note = new { original = (string?)null, value = "x" } },
        })).ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await WidgetCount());
    }

    [Fact]
    public async Task A_null_extension_channel_is_invalid_input()
    {
        var actor = host.Actor("demo", "widgets.create");
        (await actor.ExecuteAsync("widgets.create", RawWidgetBody("null")))
            .ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await WidgetCount());   // the handler never ran
    }

    [Fact]
    public async Task A_non_object_extension_channel_is_invalid_input()
    {
        var actor = host.Actor("demo", "widgets.create");
        (await actor.ExecuteAsync("widgets.create", WidgetBody(extensions: new object[0])))
            .ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await WidgetCount());
    }

    [Fact]
    public async Task A_scalar_extension_entry_is_invalid_input()
    {
        var actor = host.Actor("demo", "widgets.create");
        (await actor.ExecuteAsync("widgets.create", WidgetBody(extensions: new { note = "not-a-change-object" })))
            .ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await WidgetCount());
    }

    [Fact]
    public async Task A_null_extension_entry_is_invalid_input()
    {
        var actor = host.Actor("demo", "widgets.create");
        (await actor.ExecuteAsync("widgets.create", RawWidgetBody("""{"note":null}""")))
            .ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await WidgetCount());
    }
}
