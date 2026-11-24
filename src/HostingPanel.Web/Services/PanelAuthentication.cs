using HostingPanel.Api;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using HostingPanel.Web.Localization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Localization;

namespace HostingPanel.Web.Services;

public static class PanelAuthentication
{
    public static void MapPanelAuthentication(this WebApplication app)
    {
        app.MapGet("/login", (HttpContext context, IAntiforgery csrf,
            IStringLocalizer<SharedResource> l) =>
        {
            var destination = SafeReturn(context.Request.Query["ReturnUrl"]);
            return context.User.Identity?.IsAuthenticated == true
                ? Results.LocalRedirect(destination)
                : LoginPage(context, csrf, l, destination);
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext context, IAntiforgery csrf,
            IAuthenticationProvider authentication, PanelSessionStore sessions, IStringLocalizer<SharedResource> l) =>
        {
            if (!context.Request.HasFormContentType) return Results.BadRequest();
            try { await csrf.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var username = form["username"].ToString().Trim();
            var password = form["password"].ToString();
            var destination = SafeReturn(form["returnUrl"]);
            if (username.Length is < 1 or > 64 ||
                !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ||
                string.IsNullOrWhiteSpace(password) || password.Length > 4096)
                return LoginPage(context, csrf, l, destination, 401, l["The backend rejected the sign-in."].Value);

            int status;
            try
            {
                var credentials = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
                status = await authentication.AuthenticateSessionAsync(credentials, context.RequestAborted);
            }
            catch (HttpRequestException) { status = 503; }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { status = 503; }
            if (status is 401 or 403)
                return LoginPage(context, csrf, l, destination, 401, l["The backend rejected the sign-in."].Value);
            if (status is < 200 or >= 300)
                return LoginPage(context, csrf, l, destination, 503, l["Sign-in service unavailable. Try again later."].Value);

            var remember = form["remember"] == "true";
            var expires = DateTimeOffset.UtcNow.AddHours(remember ? 168 : 12);
            sessions.Remove(context.User.FindFirst(PanelSessionStore.SessionClaim)?.Value);
            var id = sessions.Create(username, password, expires);
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, username), new Claim(PanelSessionStore.SessionClaim, id)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties
                { IsPersistent = remember, ExpiresUtc = expires, AllowRefresh = false });
            return Results.LocalRedirect(destination);
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapPost("/logout", async (HttpContext context, IAntiforgery csrf, PanelSessionStore sessions) =>
        {
            try { await csrf.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            sessions.Remove(context.User.FindFirst(PanelSessionStore.SessionClaim)?.Value);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        }).RequireAuthorization();
    }

    private static string SafeReturn(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] == '/' &&
        !value.StartsWith("//") && !value.Contains('\\') && !value.Any(char.IsControl) &&
        !value.StartsWith("/login", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("/logout", StringComparison.OrdinalIgnoreCase) ? value : "/";

    private static IResult LoginPage(HttpContext context, IAntiforgery csrf,
        IStringLocalizer<SharedResource> l, string destination, int status = 200, string? error = null)
    {
        var token = csrf.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        var enc = HtmlEncoder.Default;
        string T(string key) => enc.Encode(l[key].Value);
        var message = error is null ? "" : "<p class=\"error\" role=\"alert\">" + enc.Encode(error) + "</p>";
        var html = $$"""
            <!doctype html>
            <html lang="{{CultureInfo.CurrentUICulture.TwoLetterISOLanguageName}}">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>HostingPanel — {{T("Sign in")}}</title><link rel="stylesheet" href="/site.css"></head>
            <body><main style="min-height:100vh;display:grid;place-items:center;padding:24px">
            <section class="card" style="width:min(100%,440px);padding:32px">
            <div class="eyebrow">HostingPanel · CONTROL CENTER</div>
            <h1>{{T("Sign in")}}</h1><p>{{T("Sign in to your hosting account.")}}</p>{{message}}
            <form method="post" action="/login">
            <input type="hidden" name="{{enc.Encode(token.FormFieldName)}}" value="{{enc.Encode(token.RequestToken!)}}">
            <input type="hidden" name="returnUrl" value="{{enc.Encode(destination)}}">
            <label for="username">{{T("Username")}}</label>
            <input id="username" name="username" autocomplete="username" maxlength="64" required autofocus>
            <label for="password">{{T("Password or login key")}}</label>
            <input id="password" name="password" type="password" autocomplete="current-password" required>
            <label style="display:flex;align-items:center;gap:8px"><input type="checkbox" name="remember" value="true" checked style="width:auto">{{T("Remember me")}}</label>
            <button class="primary" style="width:100%" type="submit">{{T("Sign in")}}</button>
            </form></section></main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }
}
