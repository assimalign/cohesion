using System;
using System.Linq;
using System.Reflection;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using Assimalign.Cohesion.IdentityModel.Token.Saml;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Namespace-alignment guards for the IdentityModel family. The repo rule is that a
/// public type's namespace matches its assembly name. Each protocol branch is its own
/// assembly (<c>…Protocols</c>, <c>…Protocols.OpenIdConnect</c>, <c>…Protocols.Saml</c>),
/// so every public type must live in its assembly-name namespace or one nested beneath
/// it, keeping the namespace map in <c>docs/DESIGN.md</c> honest as protocol branches are
/// added.
/// </summary>
public sealed class IdentityModelNamespaceAlignmentTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Namespaces: Root public types should live under the assembly namespace")]
    public void RootAssembly_PublicTypes_ShouldLiveUnderTheAssemblyNamespace()
    {
        AssertPublicTypesAlign(typeof(IdentityKind).Assembly);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Namespaces: Protocols public types should live under the assembly namespace")]
    public void ProtocolsAssembly_PublicTypes_ShouldLiveUnderTheAssemblyNamespace()
    {
        AssertPublicTypesAlign(typeof(ProtocolRole).Assembly);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Namespaces: Token public types should live under the assembly namespace")]
    public void TokenAssembly_PublicTypes_ShouldLiveUnderTheAssemblyNamespace()
    {
        AssertPublicTypesAlign(typeof(IIdentityToken).Assembly);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Namespaces: JsonWebToken public types should live under the assembly namespace")]
    public void JsonWebTokenAssembly_PublicTypes_ShouldLiveUnderTheAssemblyNamespace()
    {
        AssertPublicTypesAlign(typeof(JsonWebToken).Assembly);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Namespaces: SAML token public types should live under the assembly namespace")]
    public void SamlTokenAssembly_PublicTypes_ShouldLiveUnderTheAssemblyNamespace()
    {
        AssertPublicTypesAlign(typeof(SamlToken).Assembly);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "Test-only reflection; not subject to trimming.")]
    private static void AssertPublicTypesAlign(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name;

        assemblyName.ShouldNotBeNull();

        var misaligned = assembly
            .GetExportedTypes()
            .Where(type =>
                type.Namespace is null ||
                (!string.Equals(type.Namespace, assemblyName, StringComparison.Ordinal) &&
                 !type.Namespace.StartsWith(assemblyName + ".", StringComparison.Ordinal)))
            .Select(type => type.FullName)
            .ToArray();

        misaligned.ShouldBeEmpty();
    }
}
