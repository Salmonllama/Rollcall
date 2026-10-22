// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Rollcall.Api.Tests;

public class ApiHostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Host_starts_and_answers_requests()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        // No endpoints exist yet in M0; a 404 proves the pipeline is up.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
