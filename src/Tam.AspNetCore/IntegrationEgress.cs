using System.Net;
using System.Net.Sockets;

namespace Tam.AspNetCore;

/// <summary>Tuning for the outbound integration HTTP client and retry queue (docs/25).</summary>
public sealed class TamIntegrationOptions
{
    /// <summary>
    /// Allow outbound integration requests to reach loopback, link-local and private addresses.
    /// Off by default: an integration's base URL is tenant-supplied and runs under a privileged
    /// background actor, so an un-guarded client is a server-side request forgery vector straight at
    /// the cloud metadata endpoint (169.254.169.254) and internal services. Development or a
    /// deployment whose real targets are on the private network opts in explicitly.
    /// </summary>
    public bool AllowPrivateNetwork { get; set; }

    /// <summary>First-retry delay; each further attempt doubles it up to <see cref="RetryMaxDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Backoff ceiling.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Attempts before a unit is dead-lettered (inbound inbox and outbound queue alike).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How often the outbound retry driver drains due tasks.</summary>
    public TimeSpan RetryDriverInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Trim completed transient history (dispatched outbox, processed inbox/tasks, old runs
    /// and idempotency records) older than this. Audit is never auto-trimmed — that's a compliance
    /// decision, not a housekeeping one.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often the retention janitor runs. Set <see cref="RetentionEnabled"/> false to skip it.</summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(6);

    public bool RetentionEnabled { get; set; } = true;
}

/// <summary>Raised when the egress guard refuses a destination. Surfaces as a failed run, not a crash.</summary>
public sealed class IntegrationEgressBlockedException(string host)
    : Exception($"Egress to '{host}' is blocked: resolves to a private or link-local address.");

/// <summary>
/// SSRF guard for the outbound HTTP client. Resolves the host itself and connects only to a
/// validated public address, so DNS that points at (or rebinds to) an internal IP is refused at
/// connect time — the request never leaves for the metadata service or a neighbouring pod.
/// </summary>
public static class IntegrationEgress
{
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Guard(
        TamIntegrationOptions options) =>
        async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var allowed = options.AllowPrivateNetwork
                ? addresses
                : addresses.Where(ip => !IsBlocked(ip)).ToArray();
            if (allowed.Length == 0) throw new IntegrationEgressBlockedException(host);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect to the pre-validated address set only — never re-resolve the hostname,
                // which would reopen the rebinding window between check and connect.
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

    public static bool IsBlocked(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 0                                   // 0.0.0.0/8 "this network"
                || b[0] == 10                                  // 10.0.0.0/8 private
                || (b[0] == 172 && b[1] is >= 16 and <= 31)    // 172.16.0.0/12 private
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16 private
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 link-local (metadata)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);  // 100.64.0.0/10 CGNAT
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv4MappedToIPv6) return IsBlocked(ip.MapToIPv4());
            return (b[0] & 0xFE) == 0xFC;                      // fc00::/7 unique-local
        }
        return true;   // an address family we don't understand is not one we'll reach out to
    }
}
