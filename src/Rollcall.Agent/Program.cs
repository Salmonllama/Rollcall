// SPDX-License-Identifier: GPL-3.0-or-later

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// The on-prem AD agent (Windows service, enrollment, job polling) arrives in M9.
using var host = builder.Build();
await host.RunAsync();
