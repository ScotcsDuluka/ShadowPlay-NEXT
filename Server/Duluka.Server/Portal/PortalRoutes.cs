using System.Reflection;

namespace Duluka.Server.Portal;

// Self-contained staging tester portal; no third-party resources or cookies.
public static class PortalRoutes
{
    private const string BaseName = "Duluka.Server.Portal.";

    public static void MapPortal(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/" ||
                ctx.Request.Path.StartsWithSegments("/portal", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["X-Frame-Options"] = "DENY";
                ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
                ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                ctx.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; script-src 'self'; style-src 'self'; " +
                    "connect-src 'self'; img-src 'self'; form-action 'self'; " +
                    "frame-ancestors 'none'; base-uri 'none'";
            }
            await next();
        });
        app.MapGet("/", () => Results.Redirect("/portal"));
        // The UI must distinguish disposable staging identities from the shared
        // account database. The response contains no secrets and is no-store.
        var accountMode = string.Equals(app.Configuration["Portal:AccountMode"], "main",
            StringComparison.OrdinalIgnoreCase) ? "main" : "staging";
        var registrationEnabled = accountMode != "main" ||
            app.Configuration.GetValue<bool>("PublicApi:AllowRegistration");
        app.MapGet("/portal/config", () => Results.Json(new { accountMode, registrationEnabled }));
        app.MapGet("/portal", () => ReadResource("index.html", "text/html; charset=utf-8"));
        app.MapGet("/portal/app.css", () => ReadResource("app.css", "text/css; charset=utf-8"));
        app.MapGet("/portal/app.js", () => ReadResource("app.js", "application/javascript; charset=utf-8"));
        app.MapGet("/portal/scene.css", () => ReadResource("scene.css", "text/css; charset=utf-8"));
        app.MapGet("/portal/scene.js", () => ReadResource("scene.js", "application/javascript; charset=utf-8"));
    }

    private static IResult ReadResource(string name, string contentType)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BaseName + name);
        return stream is null ? Results.NotFound() : Results.Stream(stream, contentType);
    }
}
