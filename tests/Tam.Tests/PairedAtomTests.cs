using Tam;

namespace Tam.Tests;

/// <summary>
/// The paired-atom ownership pattern (docs/28 D-AG2): the base atom is own-scoped by default,
/// the "-all" widening atom lifts it. [Widens] puts the widening atom into the catalogue; the
/// levels expand it with its base action's tier; CheckOwnershipUnless enforces the write side.
/// </summary>
public class PairedAtomTests
{
    [Operation("things.read-op")]
    [Authorize("things.read")]
    [Widens("things.read-all")]
    private static class ReadThings
    {
        public sealed record Input(string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    private static TamModel Model() => new TamModelBuilder()
        .AddOperationType(typeof(ReadThings))
        .LocaleDefaults("en", new Dictionary<string, string>
        {
            ["operations.things.read-op.title"] = "x",
            ["labels.name"] = "x",
        })
        .Build();

    [Fact]
    public void Widens_puts_the_atom_into_the_catalogue()
    {
        Assert.Contains("things.read-all", Model().Permissions);
    }

    [Fact]
    public void Widening_atoms_expand_with_their_base_actions_level()
    {
        var model = Model();
        var view = AccessLevels.Expand(model, "things", "view").ToList();
        Assert.Contains("things.read", view);
        Assert.Contains("things.read-all", view);   // read-all rides read's tier
        var manage = AccessLevels.Expand(model, "things", "manage").ToList();
        Assert.Contains("things.read-all", manage);
    }

    private sealed class NullServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static OperationContext Context(params string[] grants) => new()
    {
        Actor = new Actor("actor-1", "Actor", new HashSet<string>(grants)),
        TenantId = new TenantId("t1"),
        Source = InvocationSource.Web,
        Culture = "en",
        Services = new NullServices(),
    };

    [Fact]
    public void CheckOwnershipUnless_passes_own_rows_for_base_holders()
    {
        var result = Context("things.read").CheckOwnershipUnless("things.read-all", "actor-1");
        Assert.False(result.IsError);
    }

    [Fact]
    public void CheckOwnershipUnless_rejects_foreign_rows_without_the_widening()
    {
        var result = Context("things.read").CheckOwnershipUnless("things.read-all", "someone-else");
        Assert.True(result.IsError);
    }

    [Fact]
    public void CheckOwnershipUnless_passes_foreign_rows_with_the_widening()
    {
        var result = Context("things.read", "things.read-all")
            .CheckOwnershipUnless("things.read-all", "someone-else");
        Assert.False(result.IsError);
    }
}
