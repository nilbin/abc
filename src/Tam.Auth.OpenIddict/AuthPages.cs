using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Tam.Auth;

/// <summary>
/// The framework-owned interactive auth pages (docs/26): a minimal, self-contained, localized login
/// and tenant picker so every Tam app gets a sign-in surface for free without shipping display text
/// in code — every string comes from the locale catalog (L10N). Inline styles only; no assets.
/// </summary>
internal static class AuthPages
{
    private static string T(TamModel model, string key, string culture) =>
        model.Locales.Lookup(key, culture) ?? key;

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Style =
        "body{font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:#f6f7f9;" +
        "margin:0;display:flex;min-height:100vh;align-items:center;justify-content:center}" +
        ".card{background:#fff;border:1px solid #e6e8eb;border-radius:12px;padding:28px;width:320px;" +
        "box-shadow:0 1px 3px rgba(0,0,0,.06)}.brand{text-align:center;color:#4c5bd4;font-size:22px;" +
        "font-weight:700;margin-bottom:18px}label{display:block;font-size:13px;color:#40474f;margin:12px 0 4px}" +
        "input[type=email],input[type=password]{width:100%;box-sizing:border-box;padding:9px 10px;" +
        "border:1px solid #d3d7db;border-radius:8px;font-size:14px}button{width:100%;margin-top:18px;" +
        "padding:10px;background:#4c5bd4;color:#fff;border:0;border-radius:8px;font-size:14px;cursor:pointer}" +
        ".err{color:#c02b2b;font-size:13px;margin-top:12px}.opt{display:flex;align-items:center;gap:10px;" +
        "padding:11px;border:1px solid #d3d7db;border-radius:8px;margin-top:10px;cursor:pointer}" +
        ".opt:hover{border-color:#4c5bd4}";

    public static string LoginPage(TamModel model, string culture, string returnUrl, bool error)
    {
        var err = error
            ? $"<div class=err>{Enc(T(model, "auth.failed", culture))}</div>"
            : "";
        return $"""
        <!doctype html><html lang="{Enc(culture)}"><head><meta charset=utf-8>
        <meta name=viewport content="width=device-width,initial-scale=1">
        <title>{Enc(T(model, "auth.sign-in", culture))}</title><style>{Style}</style></head><body>
        <form class=card method=post action="/connect/authorize/login">
          <div class=brand>&#9670; {Enc(T(model, "app.title", culture))}</div>
          <input type=hidden name=returnUrl value="{Enc(returnUrl)}">
          <label>{Enc(T(model, "auth.email", culture))}</label>
          <input type=email name=email autofocus autocomplete=username>
          <label>{Enc(T(model, "labels.password", culture))}</label>
          <input type=password name=password autocomplete=current-password>
          {err}
          <button type=submit>{Enc(T(model, "auth.sign-in", culture))}</button>
        </form></body></html>
        """;
    }

    public static string TenantPicker(
        TamModel model, string culture, IQueryCollection query, IReadOnlyList<(string Id, string Display)> tenants)
    {
        // Carry every original OAuth parameter forward as a hidden field; the chosen tenant is added
        // as one more, so re-hitting the authorization endpoint completes with the account already
        // signed in via the cookie.
        var hidden = new StringBuilder();
        foreach (var (key, value) in query)
        {
            if (key == "tenant") continue;
            hidden.Append($"<input type=hidden name=\"{Enc(key)}\" value=\"{Enc(value.ToString())}\">");
        }
        var options = new StringBuilder();
        foreach (var (id, display) in tenants)
            options.Append(
                $"<label class=opt><input type=radio name=tenant value=\"{Enc(id)}\" required> {Enc(display)}</label>");
        return $"""
        <!doctype html><html lang="{Enc(culture)}"><head><meta charset=utf-8>
        <meta name=viewport content="width=device-width,initial-scale=1">
        <title>{Enc(T(model, "auth.pick-tenant", culture))}</title><style>{Style}</style></head><body>
        <form class=card method=get action="/connect/authorize">
          <div class=brand>&#9670; {Enc(T(model, "app.title", culture))}</div>
          <div style="font-size:14px;color:#40474f">{Enc(T(model, "auth.pick-tenant", culture))}</div>
          {hidden}{options}
          <button type=submit>{Enc(T(model, "auth.continue", culture))}</button>
        </form></body></html>
        """;
    }

    public static string Message(TamModel model, string culture, string key) => $"""
        <!doctype html><html lang="{Enc(culture)}"><head><meta charset=utf-8>
        <meta name=viewport content="width=device-width,initial-scale=1">
        <title>{Enc(T(model, "app.title", culture))}</title><style>{Style}</style></head><body>
        <div class=card><div class=brand>&#9670; {Enc(T(model, "app.title", culture))}</div>
        <div style="font-size:14px;color:#40474f">{Enc(T(model, key, culture))}</div></div></body></html>
        """;
}
