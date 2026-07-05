using System;
using System.Linq;
using System.Reflection;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using Assimalign.Cohesion.IdentityModel.Token.Saml;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Dependency-direction guards for the IdentityModel family. The root package is the
/// dependency anchor: it must stay consumable by every resource without dragging in any
/// other Cohesion assembly. The protocol projects layer on the root through the shared
/// <c>Protocols</c> base; the token packages layer on the root; siblings never reference
/// each other; and nothing in the family may reference <c>Microsoft.Extensions.*</c>.
/// These tests keep the boundaries documented in <c>docs/DESIGN.md</c> honest.
/// </summary>
public sealed class IdentityModelFamilyBoundaryTests
{
    private const string RootAssemblyName = "Assimalign.Cohesion.IdentityModel";
    private const string ProtocolsAssemblyName = "Assimalign.Cohesion.IdentityModel.Protocols";
    private const string OpenIdConnectAssemblyName = "Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect";
    private const string SamlProtocolAssemblyName = "Assimalign.Cohesion.IdentityModel.Protocols.Saml";
    private const string TokenAssemblyName = "Assimalign.Cohesion.IdentityModel.Token";
    private const string JsonWebTokenAssemblyName = "Assimalign.Cohesion.IdentityModel.Token.JsonWebToken";
    private const string SamlTokenAssemblyName = "Assimalign.Cohesion.IdentityModel.Token.Saml";

    private static Assembly RootAssembly => typeof(IdentityKind).Assembly;
    private static Assembly ProtocolsAssembly => typeof(ProtocolRole).Assembly;
    private static Assembly OpenIdConnectAssembly => typeof(OpenIdConnectIdToken).Assembly;

    // Fully qualified: the Protocols.Saml and Token.Saml namespaces share short type-name
    // prefixes, so an unqualified anchor would be ambiguous under both usings.
    private static Assembly SamlProtocolAssembly =>
        typeof(Protocols.Saml.SamlConstants).Assembly;

    private static Assembly TokenAssembly => typeof(IIdentityToken).Assembly;
    private static Assembly JsonWebTokenAssembly => typeof(JsonWebToken).Assembly;
    private static Assembly SamlTokenAssembly => typeof(SamlToken).Assembly;

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Assembly names should be stable")]
    public void FamilyAssemblies_WhenInspected_ShouldHaveStableNames()
    {
        RootAssembly.GetName().Name.ShouldBe(RootAssemblyName);
        ProtocolsAssembly.GetName().Name.ShouldBe(ProtocolsAssemblyName);
        OpenIdConnectAssembly.GetName().Name.ShouldBe(OpenIdConnectAssemblyName);
        SamlProtocolAssembly.GetName().Name.ShouldBe(SamlProtocolAssemblyName);
        TokenAssembly.GetName().Name.ShouldBe(TokenAssemblyName);
        JsonWebTokenAssembly.GetName().Name.ShouldBe(JsonWebTokenAssemblyName);
        SamlTokenAssembly.GetName().Name.ShouldBe(SamlTokenAssemblyName);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Root should reference no Cohesion assemblies")]
    public void RootAssembly_WhenInspected_ShouldReferenceNoCohesionAssemblies()
    {
        var references = GetCohesionReferences(RootAssembly);

        references.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Protocols should reference only the root")]
    public void ProtocolsAssembly_WhenInspected_ShouldReferenceOnlyTheRoot()
    {
        var references = GetCohesionReferences(ProtocolsAssembly);

        references.ShouldContain(RootAssemblyName);
        references.ShouldBeSubsetOf(new[] { RootAssemblyName });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: OpenIdConnect should reference only its parent chain")]
    public void OpenIdConnectAssembly_WhenInspected_ShouldReferenceOnlyItsParentChain()
    {
        var references = GetCohesionReferences(OpenIdConnectAssembly);

        references.ShouldContain(ProtocolsAssemblyName);
        references.ShouldBeSubsetOf(new[] { RootAssemblyName, ProtocolsAssemblyName });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: SAML protocol should reference only its parent chain")]
    public void SamlProtocolAssembly_WhenInspected_ShouldReferenceOnlyItsParentChain()
    {
        var references = GetCohesionReferences(SamlProtocolAssembly);

        references.ShouldContain(ProtocolsAssemblyName);
        references.ShouldBeSubsetOf(new[] { RootAssemblyName, ProtocolsAssemblyName });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Sibling protocol packages should not reference each other")]
    public void SiblingProtocolAssemblies_WhenInspected_ShouldNotReferenceEachOther()
    {
        GetCohesionReferences(OpenIdConnectAssembly).ShouldNotContain(SamlProtocolAssemblyName);
        GetCohesionReferences(SamlProtocolAssembly).ShouldNotContain(OpenIdConnectAssemblyName);
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

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: SAML token should reference only its parent chain")]
    public void SamlTokenAssembly_WhenInspected_ShouldReferenceOnlyItsParentChain()
    {
        var references = GetCohesionReferences(SamlTokenAssembly);

        references.ShouldContain(TokenAssemblyName);
        references.ShouldBeSubsetOf(new[] { RootAssemblyName, TokenAssemblyName });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Protocol and token branches should not cross-reference")]
    public void ProtocolAndTokenBranches_WhenInspected_ShouldNotCrossReference()
    {
        // The two branches off the root are independent: the protocol contracts do not
        // depend on the token document packages, nor the reverse.
        var openIdReferences = GetCohesionReferences(OpenIdConnectAssembly);
        openIdReferences.ShouldNotContain(TokenAssemblyName);
        openIdReferences.ShouldNotContain(JsonWebTokenAssemblyName);

        var samlProtocolReferences = GetCohesionReferences(SamlProtocolAssembly);
        samlProtocolReferences.ShouldNotContain(TokenAssemblyName);
        samlProtocolReferences.ShouldNotContain(SamlTokenAssemblyName);

        GetCohesionReferences(JsonWebTokenAssembly).ShouldNotContain(OpenIdConnectAssemblyName);
        GetCohesionReferences(JsonWebTokenAssembly).ShouldNotContain(ProtocolsAssemblyName);
        GetCohesionReferences(SamlTokenAssembly).ShouldNotContain(ProtocolsAssemblyName);
        GetCohesionReferences(SamlTokenAssembly).ShouldNotContain(SamlProtocolAssemblyName);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: Sibling token packages should not reference each other")]
    public void SiblingTokenAssemblies_WhenInspected_ShouldNotReferenceEachOther()
    {
        GetCohesionReferences(JsonWebTokenAssembly).ShouldNotContain(SamlTokenAssemblyName);
        GetCohesionReferences(SamlTokenAssembly).ShouldNotContain(JsonWebTokenAssemblyName);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Family: No assembly should reference Microsoft.Extensions")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "Test-only reflection; not subject to trimming.")]
    public void FamilyAssemblies_WhenInspected_ShouldNotReferenceMicrosoftExtensions()
    {
        Assembly[] family =
        [
            RootAssembly,
            ProtocolsAssembly,
            OpenIdConnectAssembly,
            SamlProtocolAssembly,
            TokenAssembly,
            JsonWebTokenAssembly,
            SamlTokenAssembly,
        ];

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
