// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Domain.Tests;

/// <summary>
/// Guards the project layering from implementation-plan.md section 3: Contracts depends on nothing in
/// Rollcall, and Domain has no I/O, so it may reference Contracts only.
/// </summary>
public class LayeringTests
{
    [Fact]
    public void Contracts_references_no_other_rollcall_assembly()
    {
        Assert.Empty(RollcallReferences(typeof(Contracts.AssemblyMarker)));
    }

    [Fact]
    public void Domain_references_only_contracts()
    {
        var references = RollcallReferences(typeof(AssemblyMarker));

        Assert.All(references, name => Assert.Equal("Rollcall.Contracts", name));
    }

    private static IEnumerable<string?> RollcallReferences(Type marker) =>
        marker.Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null && name.StartsWith("Rollcall.", StringComparison.Ordinal));
}
