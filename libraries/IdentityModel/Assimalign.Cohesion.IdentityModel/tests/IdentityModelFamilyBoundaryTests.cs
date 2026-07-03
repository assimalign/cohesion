using System;
using System.Linq;
using System.Reflection;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using Assimalign.Cohesion.IdentityModel.Token.Saml;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Dependency-direction guards for the IdentityModel family. The root package is the
/// dependency anchor: it must stay consumable by every resource without dragging in any
/// other Cohesion assembly. Descendant token packages depend one-way toward the root and
/// never on a sibling, and nothing in the family may reference
/// <c>Microsoft.Extensions.*</c>. These tests keep the boundaries documented in
/// <c>docs/DESIGN.md</c> honest.
/// </summary>
public sealed class IdentityModelFamilyBoundaryTests
{
    private const string RootAssemblyName = "Assimalign.Cohesion.IdentityModel";
    private const string TokenAssemblyName = "Assimalign.Cohesion.IdentityModel.Token";
    private const string JsonWebTokenAssemblyName = "Assimalign.Cohesion.IdentityModel.Token.JsonWebToken";
    private const string SamlAssemblyName = "Assimalign.Cohesion.IdentityModel.Token.Saml";

    private static Assembly RootAssembly => typeof(IdentityKind).Assembly;
    private static Assembly TokenAssembly => typeof(IIdentityToken).Assembly;
    private static Assembly JsonWebTokenAssembly => typeof(JsonWebToken).Assembly;
    private static Assembly SamlAssembly => typeof(SamlToken).Assembly;

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Assembly names should be stable")]
    public void FamilyAssemblies_WhenInspected_ShouldHaveStableNames()
    {
        RootAssembly.GetName().Name.ShouldBe(RootAssemblyName);
        TokenAssembly.GetName().Name.ShouldBe(TokenAssemblyName);
        JsonWebTokenAssembly.GetName().Name.ShouldBe(JsonWebTokenAssemblyName);
        SamlAssembly.GetName().Name.ShouldBe(SamlAssemblyName);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Root should reference no Cohesion assemblies")]
    public void RootAssembly_WhenInspected_ShouldReferenceNoCohesionAssemblies()
    {
        var references = GetCohesionReferences(RootAssembly);

        references.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Token should reference only the root")]
    public void TokenAssembly_WhenInspected_ShouldReferenceOnlyTheRoot()
    {
        // The emitted metadata only records references the compiler actually used, so this
        // stays a subset check until [L01.01.12.06] aligns the token contracts with the
        // root model and the root reference becomes a required entry.
        var references = GetCohesionReferences(TokenAssembly);

        references.ShouldBeSubsetOf(new[] { RootAssemblyName });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: JsonWebToken should reference only its parent chain")]
    public void JsonWebTokenAssembly_WhenInspected_ShouldReferenceOnlyItsParentChain()
    {
        var references = GetCohesionReferences(JsonWebTokenAssembly);

        references.ShouldContain(TokenAssemblyName);
        references.ShouldBeSubsetOf(new[] { RootAssemblyName, TokenAssemblyName });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Saml should reference only its parent chain")]
    public void SamlAssembly_WhenInspected_ShouldReferenceOnlyItsParentChain()
    {
        var references = GetCohesionReferences(SamlAssembly);

        references.ShouldContain(TokenAssemblyName);
        references.ShouldBeSubsetOf(new[] { RootAssemblyName, TokenAssemblyName });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Sibling token packages should not reference each other")]
    public void SiblingTokenAssemblies_WhenInspected_ShouldNotReferenceEachOther()
    {
        GetCohesionReferences(JsonWebTokenAssembly).ShouldNotContain(SamlAssemblyName);
        GetCohesionReferences(SamlAssembly).ShouldNotContain(JsonWebTokenAssemblyName);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: No assembly should reference Microsoft.Extensions")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "Test-only reflection; not subject to trimming.")]
    public void FamilyAssemblies_WhenInspected_ShouldNotReferenceMicrosoftExtensions()
    {
        Assembly[] family = [RootAssembly, TokenAssembly, JsonWebTokenAssembly, SamlAssembly];

        foreach (var assembly in family)
        {
            assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .ShouldAllBe(name => name != null && !name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal));
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "Test-only reflection; not subject to trimming.")]
    private static string[] GetCohesionReferences(Assembly assembly)
    {
        return assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("Assimalign.Cohesion", StringComparison.Ordinal))
            .Select(name => name!)
            .ToArray();
    }
}
