using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace LISSTech.EntitySync.Scheduler;

internal static class EntitySyncSchedulerDashboardEndpoints
{
    internal static void RequireDashboardAuthenticationForAssets(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/assets")
                && context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync();
                return;
            }

            await next(context);
        });
    }

    internal static void MapDashboard(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var dashboardRoot = ResolveDashboardRoot(app.Environment.ContentRootPath);
        var indexHtml = File.ReadAllText(Path.Combine(dashboardRoot, "index.html"));
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(dashboardRoot),
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            }
        });

        app.MapGet("/", (HttpContext context) =>
            {
                SetNoStoreHeaders(context.Response);
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'; form-action 'none'; object-src 'none'";
                context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
                context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.Content(indexHtml, "text/html; charset=utf-8");
            })
            .RequireAuthorization(EntitySyncSchedulerDashboardAuthentication.PolicyName);

        app.MapGet("/dashboard", () => Results.Redirect("/", permanent: false))
            .RequireAuthorization(EntitySyncSchedulerDashboardAuthentication.PolicyName);
        app.MapGet(
                "/dashboard/data",
                (HttpContext context,
                    EntitySyncSchedulerDashboardStore dashboard,
                    EntitySyncSchedulerStatus status,
                    EntitySyncSchedulerOptions options) =>
                {
                    SetNoStoreHeaders(context.Response);
                    return Results.Ok(dashboard.Snapshot(status.Snapshot, options));
                })
            .RequireAuthorization(EntitySyncSchedulerDashboardAuthentication.PolicyName);
    }

    private static string ResolveDashboardRoot(string contentRoot)
    {
        var startingPoints = new[]
        {
            AppContext.BaseDirectory,
            contentRoot,
            Directory.GetCurrentDirectory()
        };
        foreach (var startingPoint in startingPoints)
        {
            for (var directory = new DirectoryInfo(startingPoint);
                 directory is not null;
                 directory = directory.Parent)
            {
                var direct = Path.Combine(directory.FullName, "wwwroot");
                if (File.Exists(Path.Combine(direct, "index.html"))) return direct;

                var repository = Path.Combine(directory.FullName, "scheduler", "wwwroot");
                if (File.Exists(Path.Combine(repository, "index.html"))) return repository;
            }
        }

        throw new InvalidOperationException(
            "The scheduler dashboard assets are missing. Build scheduler-ui before starting the scheduler.");
    }

    private static void SetNoStoreHeaders(HttpResponse response) =>
        response.Headers.CacheControl = "no-store, max-age=0";
}
