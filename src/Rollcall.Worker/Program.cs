// SPDX-License-Identifier: GPL-3.0-or-later

using Rollcall.Infrastructure;
using Rollcall.Worker;

// The container healthcheck runs "dotnet Rollcall.Worker.dll healthcheck".
if (args is ["healthcheck", ..])
{
    return HeartbeatService.IsLocallyHealthy(TimeProvider.System) ? 0 : 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddRollcallDatabase();
builder.Services.AddRollcallCoreServices();
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<WorkflowWorkerService>();
builder.Services.AddHostedService<SchedulerBackgroundService>();

using var host = builder.Build();
await host.RunAsync();
return 0;
