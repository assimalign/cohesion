using System;
using System.Linq;
using System.Reflection;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// Dependency-direction guards for the HTTP protocol core. The core
/// (<c>Assimalign.Cohesion.Http</c>) must remain usable by every consumer that wants raw
/// HTTP contracts &#8211; clients, proxies, observability layers, DNS-over-HTTPS
/// transports. Application-layer features (sessions, parsed forms, identity, &#8230;) live
/// in sibling packages and layer on top; they must never appear in the core's reference
/// graph.
/// </summary>
public class HttpProtocolCoreBoundaryTests
{
    [Fact]
    public void Http_AssemblyName_ShouldBeStable()
    {
        Assembly httpCore = typeof(IHttpRequest).Assembly;

        httpCore.GetName().Name.ShouldBe("Assimalign.Cohesion.Http");
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "Test-only reflection; not subject to trimming.")]
    public void Http_ShouldNotReferenceSessionsPackage()
    {
        Assembly httpCore = typeof(IHttpRequest).Assembly;

        AssemblyName[] references = httpCore.GetReferencedAssemblies();

        references
            .Select(r => r.Name)
            .ShouldNotContain("Assimalign.Cohesion.Http.Sessions");
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "Test-only reflection; not subject to trimming.")]
    public void Http_ShouldNotReferenceFormsPackage()
    {
        Assembly httpCore = typeof(IHttpRequest).Assembly;

        AssemblyName[] references = httpCore.GetReferencedAssemblies();

        references
            .Select(r => r.Name)
            .ShouldNotContain("Assimalign.Cohesion.Http.Forms");
    }

    [Fact]
    public void IHttpRequest_ShouldNotExposeFormProperty()
    {
        // Moved to Assimalign.Cohesion.Http.Forms via HttpRequestFormExtensions.
        Type requestInterface = typeof(IHttpRequest);

        requestInterface.GetProperty("Form").ShouldBeNull();
    }

    [Fact]
    public void IHttpContext_ShouldNotExposeSessionProperty()
    {
        // Moved to Assimalign.Cohesion.Http.Sessions via HttpContextSessionExtensions.
        Type contextInterface = typeof(IHttpContext);

        contextInterface.GetProperty("Session").ShouldBeNull();
    }

    [Fact]
    public void IHttpContext_ShouldExposeItemsBag()
    {
        // The Items bag is how higher-layer packages attach session, form, identity, etc.
        Type contextInterface = typeof(IHttpContext);

        PropertyInfo? itemsProperty = contextInterface.GetProperty("Items");

        itemsProperty.ShouldNotBeNull();
        itemsProperty!.PropertyType.Name.ShouldStartWith("IDictionary");
    }
}
