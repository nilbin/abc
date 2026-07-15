using System.Net;
using Tam;
using Tam.AspNetCore;

namespace Tam.Tests;

/// <summary>Round-2 review hardening (docs/25): reserved grants and SSRF egress.</summary>
public class ReservedPermissionTests
{
    private static Actor Wildcard() => new("root", "Root", new HashSet<string> { "*" });

    [Fact]
    public void Wildcard_grants_ordinary_permissions()
    {
        Assert.True(Wildcard().Can("orders.read"));
        Assert.True(Wildcard().Can("plugins.manage"));
    }

    [Fact]
    public void Wildcard_does_not_grant_reserved_permissions()
    {
        // The self-service entitlement bypass: a tenant admin ("*") — or a plugin running as the
        // system actor ("*") — must NOT be able to change the tenant's own subscription.
        Assert.False(Wildcard().Can("subscriptions.manage"));
    }

    [Fact]
    public void Reserved_permissions_still_work_when_granted_explicitly()
    {
        var billing = new Actor("billing", "Billing", new HashSet<string> { "subscriptions.manage" });
        Assert.True(billing.Can("subscriptions.manage"));
    }
}

public class EgressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("169.254.169.254")]  // cloud metadata endpoint
    [InlineData("10.0.0.5")]         // private
    [InlineData("172.16.4.4")]       // private
    [InlineData("192.168.1.1")]      // private
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("::1")]              // IPv6 loopback
    [InlineData("fd00::1")]          // IPv6 unique-local
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback
    [InlineData("192.0.0.1")]        // IETF protocol assignments 192.0.0.0/24
    [InlineData("198.18.0.1")]       // benchmarking 198.18.0.0/15
    [InlineData("198.19.5.5")]       // benchmarking 198.18.0.0/15
    [InlineData("255.255.255.255")]  // limited broadcast
    public void Internal_addresses_are_blocked(string ip) =>
        Assert.True(IntegrationEgress.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("93.184.216.34")]    // example.com
    [InlineData("8.8.8.8")]          // public DNS
    [InlineData("172.32.0.1")]       // just outside 172.16/12
    public void Public_addresses_are_allowed(string ip) =>
        Assert.False(IntegrationEgress.IsBlocked(IPAddress.Parse(ip)));
}
