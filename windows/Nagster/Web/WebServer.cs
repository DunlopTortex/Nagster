using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nagster.Web;

/// <summary>
/// Hosts the web interface on 0.0.0.0 (all network interfaces) using Kestrel. Every API call except
/// sign-in needs a session cookie, and every state-changing call must carry a custom header, which
/// browsers won't send cross-site without a CORS preflight we never approve (CSRF protection).
/// </summary>
public sealed class WebServer(App alarms, IPAddress? bindAddress = null, ITextClipboard? clipboard = null, WebSessions? sessions = null)
{
    /// <summary>Request size allowed for clipboard text; everything else keeps Kestrel's small default limit.</summary>
    public const long ClipboardBodyLimit = 4 * 1024 * 1024;

    /// <summary>Request size allowed for a sync, enough for the actions of a phone that was away for weeks.</summary>
    public const long SyncBodyLimit = 1024 * 1024;

    private readonly ITextClipboard _clipboard = clipboard ?? new SystemTextClipboard();
    // All interfaces by default so phones on the network can connect; tests bind to loopback instead.
    private readonly IPAddress _bindAddress = bindAddress ?? IPAddress.Any;

    public const string SessionCookie = "dgna_session";
    public const string CsrfHeader = "X-DGNA";

    private readonly WebSessions _sessions = sessions ?? new WebSessions();

    /// <summary>Signed-in devices, for Settings.</summary>
    public WebSessions Sessions => _sessions;

    /// <summary>Sends the session cookie: HttpOnly so page scripts can't read it, SameSite=Strict against CSRF.</summary>
    public static void SetSessionCookie(HttpContext context, string token) =>
        context.Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = WebSessions.SessionLifetime
        });
    private WebApplication? _host;
    private int _port;

    /// <summary>Human-readable state for Settings, e.g. "Running on port 5972" or why it couldn't start.</summary>
    public string Status { get; private set; } = "Off";

    /// <summary>Starts, restarts or stops the server to match the settings.</summary>
    public async Task ApplyAsync(WebAccessSettings settings, bool passwordChanged)
    {
        if (passwordChanged) _sessions.EndAll();
        var wanted = settings.Enabled && settings.PasswordHash.Length > 0;
        if (_host is not null && wanted && _port == settings.Port) return;
        await StopAsync();
        if (!wanted)
        {
            Status = settings.Enabled ? "Off: set a password to turn it on" : "Off";
            return;
        }
        try
        {
            _host = Build(settings.Port);
            await _host.StartAsync();
            _port = settings.Port;
            Status = $"Running on port {settings.Port}";
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            await StopAsync();
            // Kestrel reports a port clash as an IOException (AddressInUseException).
            Status = $"Couldn't start on port {settings.Port}: " +
                     (ex is IOException ? "another program is already using that port." : ex.Message);
        }
    }

    public async Task StopAsync()
    {
        if (_host is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _host.StopAsync(timeout.Token); }
        finally
        {
            await _host.DisposeAsync();
            _host = null;
        }
    }

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(_bindAddress, port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 16 * 1024;
        });
        var app = builder.Build();
        app.Use(SecurityHeaders);
        app.Use(ReportFailures);
        app.Use(RequireSessionForApi);
        WebApi.Map(app, alarms, _sessions, _clipboard);
        return app;
    }

    private static async Task SecurityHeaders(HttpContext context, RequestDelegate next)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
        // The page's single inline script and stylesheet are allowed by a per-response nonce.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items["nonce"] = nonce;
        headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; img-src 'self' data:; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        await next(context);
    }

    /// <summary>Turns an unexpected error into a JSON message the page can show, instead of a bare HTTP 500.</summary>
    private static async Task ReportFailures(HttpContext context, RequestDelegate next)
    {
        try { await next(context); }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            System.Diagnostics.Debug.WriteLine($"Web interface error on {context.Request.Path}: {ex}");
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Something went wrong in the alarm app. Check the PC and try again." });
        }
    }

    private async Task RequireSessionForApi(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api")) { await next(context); return; }
        if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers[CsrfHeader] != "1")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var token = context.Request.Cookies[SessionCookie];
        if (path != "/api/login" && !_sessions.IsValid(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        // Using a device keeps it signed in: the session and the cookie's expiry are pushed back (at most daily).
        if (token is not null && path != "/api/logout" && _sessions.Touch(token)) SetSessionCookie(context, token);
        // Only a signed-in request may send a large body, and only to set the clipboard or sync.
        var isSync = path == "/api/sync";
        if (HttpMethods.IsPost(context.Request.Method) && (isSync || path == "/api/clipboard") &&
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = isSync ? SyncBodyLimit : ClipboardBodyLimit;
        await next(context);
    }
}
