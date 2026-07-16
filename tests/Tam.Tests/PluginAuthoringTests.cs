using Tam;

namespace Tam.Tests;

/// <summary>
/// The authoring reshape (review round 4): behaviors register from their own attributes
/// ([Gate]/[GateAll]/[OnEffect], the add-by-type substrate AddDiscovered emits), and big
/// plugins compose Configure from explicit parts.
/// </summary>
public class PluginAuthoringTests
{
    [Operation("things.create")]
    [Authorize("things.manage")]
    private static class CreateThing
    {
        public sealed record Input([property: LabelKey("labels.name")] string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    private sealed class ProbeGate : IOperationGate
    {
        public Task<Result> CheckAsync(GateContext gate, CancellationToken ct) =>
            Task.FromResult(Result.Success());
    }

    [Gate("things.create", Pure = true)]
    private sealed class AttributedGate : ProbeShape, IOperationGate
    {
        public Task<Result> CheckAsync(GateContext gate, CancellationToken ct) =>
            Task.FromResult(Result.Success());
    }

    [GateAll]
    private sealed class AttributedWildcard : IOperationGate
    {
        public Task<Result> CheckAsync(GateContext gate, CancellationToken ct) =>
            Task.FromResult(Result.Success());
    }

    [OnEffect("demo.first"), OnEffect("demo.second")]
    private sealed class AttributedSubscriber : IEffectHandler
    {
        public Task HandleAsync(EffectEvent effect, CancellationToken ct) => Task.CompletedTask;
    }

    private class ProbeShape;

    [Gate("things.create")]
    private sealed class NotAGate;   // attributed but wrong shape → PLG012

    private sealed class ContractPart : IPluginPart
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.PublishesEvent("demo.first", "id");
    }

    private sealed class SurfacePart : IPluginPart
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.PublishesEvent("demo.second", "id");
    }

    [TamPlugin("demo")]
    private sealed class PartsPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.demo.title"] = "Demo",
            });
            // What AddDiscovered would emit for the attributed classes:
            plugin.AddGateType(typeof(AttributedGate));
            plugin.AddGateType(typeof(AttributedWildcard));
            plugin.AddSubscriberType(typeof(AttributedSubscriber));
            // Parts compose the declarations (explicitly — Configure is the index):
            plugin.AddPart<ContractPart>();
            plugin.AddPart<SurfacePart>();
        }
    }

    [Fact]
    public void Attributed_behaviors_register_with_target_purity_and_plugin_tag()
    {
        var model = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["operations.things.create.title"] = "Create",
                ["labels.name"] = "Name",
            })
            .AddOperationType(typeof(CreateThing))
            .AddPlugin<PartsPlugin>()
            .Build();

        var gate = model.Gates["things.create"].Single(g => g.HandlerType == typeof(AttributedGate));
        Assert.True(gate.Pure);
        Assert.Equal("demo", gate.PluginId);

        Assert.Contains(model.Gates[GateDefinition.Wildcard],
            g => g.HandlerType == typeof(AttributedWildcard) && !g.Pure);

        // One [OnEffect] per subscription — both events, same handler, plugin-tagged.
        Assert.Contains(model.Subscribers,
            s => s.EventType == "demo.first" && s.HandlerType == typeof(AttributedSubscriber));
        Assert.Contains(model.Subscribers,
            s => s.EventType == "demo.second" && s.HandlerType == typeof(AttributedSubscriber));

        // The parts' declarations landed under the plugin like any Configure line.
        Assert.Equal("demo", model.Events["demo.first"].Plugin);
        Assert.Equal("demo", model.Events["demo.second"].Plugin);
    }

    [Fact]
    public void PLG012_rejects_shapeless_or_unattributed_types()
    {
        var builder = new TamModelBuilder();
        // Reaching the add-by-type methods outside plugin scope is PLG005 (host has no gates).
        Assert.StartsWith("PLG012", Assert.Throws<InvalidOperationException>(
            () => builder.AddGateType(typeof(ProbeGate))).Message);       // no attribute
        Assert.StartsWith("PLG012", Assert.Throws<InvalidOperationException>(
            () => builder.AddGateType(typeof(NotAGate))).Message);        // not an IOperationGate
        Assert.StartsWith("PLG012", Assert.Throws<InvalidOperationException>(
            () => builder.AddSubscriberType(typeof(ProbeGate))).Message); // no [OnEffect]
    }

    [Fact]
    public void Add_by_type_requires_plugin_scope()
    {
        // A host assembly with a [Gate] class fails loudly at AddDiscovered, not silently.
        Assert.StartsWith("PLG005", Assert.Throws<InvalidOperationException>(
            () => new TamModelBuilder().AddGateType(typeof(AttributedGate))).Message);
    }
}
