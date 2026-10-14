using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// Unit coverage for the endpoint description carriers, <see cref="EndpointParameterMetadata"/> and
/// <see cref="EndpointResponseMetadata"/>: what they expose and the invariants their constructors guard.
/// </summary>
public class EndpointDescriptionMetadataTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description metadata: a parameter description exposes its arguments")]
    public void EndpointParameterMetadata_ValidArguments_ShouldExposeThem()
    {
        // Act
        EndpointParameterMetadata metadata = new("X-Tenant", EndpointParameterSource.Header, typeof(string), isRequired: true);

        // Assert
        metadata.Name.ShouldBe("X-Tenant");
        metadata.Source.ShouldBe(EndpointParameterSource.Header);
        metadata.Type.ShouldBe(typeof(string));
        metadata.IsRequired.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description metadata: a parameter description rejects an empty name")]
    public void EndpointParameterMetadata_EmptyName_ShouldThrow()
    {
        // Act
        Action act = () => _ = new EndpointParameterMetadata("", EndpointParameterSource.Query, typeof(int), isRequired: false);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description metadata: a parameter description rejects a null type")]
    public void EndpointParameterMetadata_NullType_ShouldThrow()
    {
        // Act
        Action act = () => _ = new EndpointParameterMetadata("id", EndpointParameterSource.Route, null!, isRequired: true);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description metadata: a parameter description rejects an undefined source")]
    public void EndpointParameterMetadata_UndefinedSource_ShouldThrow()
    {
        // Act
        Action act = () => _ = new EndpointParameterMetadata("id", (EndpointParameterSource)42, typeof(int), isRequired: true);

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description metadata: a response description defaults to no body")]
    public void EndpointResponseMetadata_StatusOnly_ShouldDescribeNoBody()
    {
        // Act
        EndpointResponseMetadata metadata = new(CohesionHttpStatusCode.NoContent);

        // Assert
        metadata.StatusCode.ShouldBe(CohesionHttpStatusCode.NoContent);
        metadata.Type.ShouldBeNull();
        metadata.ContentType.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description metadata: a response description exposes a fixed content type")]
    public void EndpointResponseMetadata_FixedContentType_ShouldExposeIt()
    {
        // Act
        EndpointResponseMetadata metadata = new(CohesionHttpStatusCode.Ok, typeof(string), HttpMediaType.TextPlain);

        // Assert
        metadata.Type.ShouldBe(typeof(string));
        metadata.ContentType.ShouldBe(HttpMediaType.TextPlain);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description metadata: a response description rejects an unset status code")]
    public void EndpointResponseMetadata_DefaultStatusCode_ShouldThrow()
    {
        // Act
        Action act = () => _ = new EndpointResponseMetadata(default);

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description metadata: a response description rejects a wildcard content type")]
    public void EndpointResponseMetadata_WildcardContentType_ShouldThrow()
    {
        // Act
        Action act = () => _ = new EndpointResponseMetadata(CohesionHttpStatusCode.Ok, typeof(string), HttpMediaType.Any);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }
}
