// SPDX-License-Identifier: GPL-3.0-or-later

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// The job worker (queue, schedules, workflow steps) arrives in M1 and M3.
using var host = builder.Build();
await host.RunAsync();
