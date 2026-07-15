using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;

namespace Fortnox;

public static class MockFortnox
{
    /// <summary>
    /// A stand-in for Fortnox's accounting API, so the plugin's outbound-integration loop
    /// (docs/25) is verifiable end to end without a real external system. Demo-only — a host
    /// opts in explicitly; records every push it receives.
    /// </summary>
    public static IEndpointRouteBuilder MapMockFortnox(this IEndpointRouteBuilder app)
    {
        var vouchers = new System.Collections.Concurrent.ConcurrentBag<string>();
        app.MapPost("/mock/fortnox/vouchers", async (HttpContext http) =>
        {
            if (http.Request.Headers["Access-Token"] != "seeded-secret-key") return Results.Unauthorized();
            using var reader = new StreamReader(http.Request.Body);
            vouchers.Add(await reader.ReadToEndAsync());
            return Results.Ok(new { created = true });
        });
        app.MapGet("/mock/fortnox/vouchers", () => Results.Json(vouchers.ToArray()));
        app.MapGet("/mock/fortnox/orders", () => Results.Json(Array.Empty<object>()));   // poll target
        return app;
    }
}
