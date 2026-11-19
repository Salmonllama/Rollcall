// SPDX-License-Identifier: GPL-3.0-or-later

using Rollcall.Agent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "RollcallAdAgent";
});
builder.Services.AddHttpClient("RollcallPortal", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<IDirectory, LdapActiveDirectory>();
builder.Services.AddSingleton<AgentJobProcessor>();
builder.Services.AddHostedService<AgentWorkerService>();

using var host = builder.Build();
await host.RunAsync();
