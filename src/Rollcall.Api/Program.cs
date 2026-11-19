// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Rollcall.Api;
using Rollcall.Api.Auth;
using Rollcall.Api.Endpoints;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure;
using Rollcall.Infrastructure.Health;
using Rollcall.Infrastructure.Operations;
using Rollcall.Infrastructure.Persistence;

// CLI subcommands invoked by container healthcheck, install.sh, and CI.
if (args is ["healthcheck", ..])
{
    return await HealthcheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);
if (args is ["generate-permissions-doc", ..] && string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Rollcall")))
{
    builder.Configuration["ConnectionStrings:Rollcall"] = "Host=127.0.0.1;Database=rollcall;Username=rollcall";
}

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddRollcallDatabase();
builder.Services.AddRollcallCoreServices();
builder.Services.AddScoped<PortalSessionService>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddAuthentication(PortalAuthConstants.CookieScheme)
    .AddCookie(PortalAuthConstants.CookieScheme, options =>
    {
        options.Cookie.Name = "rc_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(1);
        options.SlidingExpiration = true;
        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = context =>
            {
                // Enforce 8-hour absolute session lifetime (section 4.5).
                var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
                if (context.Properties.IssuedUtc is { } issued &&
                    clock.GetUtcNow() - issued > TimeSpan.FromHours(8))
                {
                    context.RejectPrincipal();
                }

                return Task.CompletedTask;
            },
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddOptions<CookieAuthenticationOptions>(PortalAuthConstants.CookieScheme)
    .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);
builder.Services.AddAuthorization();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

// Host CLI subcommands: admin-reset, rotate-master-key, generate-permissions-doc.
if (args is ["admin-reset", var resetUpn, ..])
{
    await using var cliScope = app.Services.CreateAsyncScope();
    await cliScope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync();
    return await cliScope.ServiceProvider.GetRequiredService<OperationsCliCommands>().AdminResetAsync(resetUpn);
}

if (args is ["rotate-master-key", ..])
{
    await using var cliScope = app.Services.CreateAsyncScope();
    await cliScope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync();
    var oldKey = Environment.GetEnvironmentVariable("ROLLCALL_OLD_MASTER_KEY") ?? string.Empty;
    var newKey = Environment.GetEnvironmentVariable("ROLLCALL_NEW_MASTER_KEY") ?? string.Empty;
    return await cliScope.ServiceProvider.GetRequiredService<OperationsCliCommands>().RotateMasterKeyAsync(oldKey, newKey);
}

if (args is ["generate-permissions-doc", var outputPath, ..])
{
    await using var cliScope = app.Services.CreateAsyncScope();
    var allSteps = cliScope.ServiceProvider.GetRequiredService<IEnumerable<Rollcall.Domain.Workflow.IWorkflowStep>>();
    return OperationsCliCommands.GeneratePermissionsDoc(allSteps, outputPath);
}

// Migrate before serving, so /healthz only answers once the schema is current.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync();
}

app.UseForwardedHeaders();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerPathFeature>();
        var ex = feature?.Error;
        var (status, title, detail) = ex switch
        {
            StepExecutionException stepEx => (StatusCodes.Status400BadRequest, "Request couldn't be completed", stepEx.PlainLanguageMessage),
            KeyNotFoundException notFound => (StatusCodes.Status404NotFound, "Not found", notFound.Message),
            UnauthorizedAccessException unauth => (StatusCodes.Status403Forbidden, "Access denied", unauth.Message),
            ArgumentException argEx => (StatusCodes.Status400BadRequest, "Invalid input", argEx.Message),
            InvalidOperationException invEx => (StatusCodes.Status409Conflict, "Conflict", invEx.Message),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "Rollcall encountered an unexpected error. Check the app logs for details."),
        };

        await Results.Problem(title: title, detail: detail, statusCode: status).ExecuteAsync(context);
    });
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// CSRF double-submit check on state-changing authenticated browser API requests (section 4.5).
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var path = context.Request.Path;
    if (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method))
    {
        var exempt = path.StartsWithSegments("/api/agent") ||
            path.StartsWithSegments("/api/setup") ||
            path.StartsWithSegments("/api/auth");

        if (!exempt && path.StartsWithSegments("/api") &&
            context.User.Identity?.IsAuthenticated == true &&
            context.Request.Cookies.TryGetValue(PortalAuthConstants.CsrfCookie, out var expectedCsrf) &&
            !string.IsNullOrWhiteSpace(expectedCsrf))
        {
            var provided = context.Request.Headers[PortalAuthConstants.CsrfHeader].FirstOrDefault();
            if (!string.Equals(provided, expectedCsrf, StringComparison.Ordinal))
            {
                await Results.Problem(
                    title: "CSRF token missing or invalid",
                    detail: "Reload the page and try that action again.",
                    statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }
        }
    }

    await next();
});

// Liveness and readiness endpoints (M1 contract preserved verbatim).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/readyz", async (ReadinessProbe probe, CancellationToken cancellationToken) =>
{
    var report = await probe.CheckAsync(cancellationToken);
    var body = new { db = report.Db, migrations = report.Migrations, worker = report.Worker };
    return report.IsReady ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapOpenApi();
app.MapSetupAndAuthEndpoints();
app.MapDirectoryEndpoints();
app.MapWorkflowEndpoints();
app.MapAdminEndpoints();
app.MapAgentProtocolEndpoints();

// SPA client-side route fallback (never intercepts /api, /healthz, /readyz, /signin-oidc).
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/signin-oidc") ||
        context.Request.Path.StartsWithSegments("/openapi"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var indexPath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexPath);
        return;
    }

    context.Response.StatusCode = StatusCodes.Status404NotFound;
});

await app.RunAsync();
return 0;

/// <summary>Entry point, exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
