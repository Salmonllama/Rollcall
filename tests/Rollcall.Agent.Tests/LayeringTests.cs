// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Agent.Tests;

/// <summary>
/// The AD agent runs on customer domain controllers and shares only the wire contracts with the portal
/// (implementation-plan.md section 4.7). It must never pull in the domain model or infrastructure.
/// </summary>
public class LayeringTests
{
    [Fact]
    public void Agent_references_only_contracts()
    {
        var references = typeof(AssemblyMarker).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null && name.StartsWith("Rollcall.", StringComparison.Ordinal));

        Assert.All(references, name => Assert.Equal("Rollcall.Contracts", name));
    }
}
