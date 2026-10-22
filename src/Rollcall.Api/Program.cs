// SPDX-License-Identifier: GPL-3.0-or-later

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

var app = builder.Build();

// Endpoints arrive in M1 (health checks) and M2 (setup and sign-in).
app.Run();

/// <summary>Entry point, exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
