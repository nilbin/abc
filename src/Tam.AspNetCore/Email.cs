using Microsoft.Extensions.Logging;

namespace Tam.AspNetCore;

/// <summary>
/// Outbound email seam: the framework composes localized messages (invites today), the deployment
/// decides transport. Replace the registration with an SMTP/API adapter in production — the default
/// only logs, which doubles as the dev inbox (the invite link is readable in the console).
/// </summary>
public interface ITamEmail
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>Dev default: the "sent" mail goes to the log, link and all.</summary>
public sealed class LogTamEmail(ILogger<LogTamEmail> logger) : ITamEmail
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        logger.LogInformation("tam-email to={To} subject={Subject} body={Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
