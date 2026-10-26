// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Api;

/// <summary>Probes the running app's <c>/healthz</c> from inside the container. Exit code 0 means healthy.</summary>
internal static class HealthcheckCommand
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/healthz"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
