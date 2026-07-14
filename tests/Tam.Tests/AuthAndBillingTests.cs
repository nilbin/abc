using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

public class AuthAndBillingTests
{
    [Fact]
    public void Password_hash_roundtrips_and_rejects_wrong_password()
    {
        var hash = TamPasswords.Hash("correct horse");
        Assert.True(TamPasswords.Verify("correct horse", hash));
        Assert.False(TamPasswords.Verify("wrong", hash));
        Assert.NotEqual(hash, TamPasswords.Hash("correct horse"));   // per-hash salt
    }

    [Fact]
    public void Password_verify_tolerates_malformed_stored_hash()
    {
        Assert.False(TamPasswords.Verify("x", ""));
        Assert.False(TamPasswords.Verify("x", "not-a-hash"));
        Assert.False(TamPasswords.Verify("x", "pbkdf2$abc$def"));
    }

    [Theory]
    [InlineData("[]", "inspect", false)]
    [InlineData("""["inspect"]""", "inspect", true)]
    [InlineData("""["inspect"]""", "fortnox", false)]
    [InlineData("""["*"]""", "anything", true)]
    public void Subscription_entitlement_matches_plan(string entitlements, string plugin, bool expected)
    {
        var subscription = new SubscriptionEntity { EntitlementsJson = entitlements };
        Assert.Equal(expected, subscription.Entitles(plugin));
    }

    [Fact]
    public void Free_default_subscription_entitles_nothing_with_a_default_seat_count()
    {
        var free = new SubscriptionEntity { TenantId = "demo" };
        Assert.False(free.Entitles("inspect"));
        Assert.True(free.Seats > 0);   // usable without a billing system
    }
}
