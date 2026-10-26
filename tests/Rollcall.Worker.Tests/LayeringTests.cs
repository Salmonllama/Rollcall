// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Worker.Tests;

/// <summary>The worker shares code with the API through Domain and Infrastructure, never by referencing the API host.</summary>
public class LayeringTests
{
    [Fact]
    public void Worker_does_not_reference_the_api()
    {
        var references = typeof(AssemblyMarker).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain("Rollcall.Api", references);
    }
}
