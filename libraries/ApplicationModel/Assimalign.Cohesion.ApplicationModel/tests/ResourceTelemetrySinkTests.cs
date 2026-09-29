using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ResourceTelemetrySinkTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Telemetry sink: FromResource captures the sink resource and endpoint")]
    public void FromResource_Descriptor_ShouldCaptureResourceAndEndpoint()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        IApplicationResourceDescriptor sink = builder.AddResource(TestManifestFactory.Create("logs"));

        // Act
        ResourceTelemetrySink defaulted = ResourceTelemetrySink.FromResource(sink);
        ResourceTelemetrySink named = ResourceTelemetrySink.FromResource(sink, "collector");

        // Assert
        defaulted.Resource.ShouldBe((ResourceName)"logs");
        defaulted.EndpointName.ShouldBe("otlp");
        defaulted.ExternalEndpoint.ShouldBeNull();
        defaulted.HeadersParameter.ShouldBeNull();
        defaulted.IsExternal.ShouldBeFalse();
        named.EndpointName.ShouldBe("collector");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Telemetry sink: FromResource by name captures the sink resource and endpoint")]
    public void FromResource_Name_ShouldCaptureResourceAndEndpoint()
    {
        // Act
        ResourceTelemetrySink defaulted = ResourceTelemetrySink.FromResource((ResourceName)"logs");
        ResourceTelemetrySink named = ResourceTelemetrySink.FromResource("logs", "collector");

        // Assert
        defaulted.Resource.ShouldBe((ResourceName)"logs");
        defaulted.EndpointName.ShouldBe("otlp");
        defaulted.IsExternal.ShouldBeFalse();
        defaulted.HeadersParameter.ShouldBeNull();
        named.Resource.ShouldBe((ResourceName)"logs");
        named.EndpointName.ShouldBe("collector");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Telemetry sink: FromResource by name rejects a blank name or endpoint")]
    public void FromResource_BlankName_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => ResourceTelemetrySink.FromResource(default(ResourceName)))
            .ParamName.ShouldBe("sink");
        Should.Throw<ArgumentException>(() => ResourceTelemetrySink.FromResource((ResourceName)" "))
            .ParamName.ShouldBe("sink");
        Should.Throw<ArgumentException>(() => ResourceTelemetrySink.FromResource((ResourceName)"logs", " "))
            .ParamName.ShouldBe("endpoint");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Telemetry sink: External captures the address and headers parameter")]
    public void External_Address_ShouldCaptureAddressAndHeaders()
    {
        // Arrange
        var address = new Uri("https://otel.example.test:4318");

        // Act
        ResourceTelemetrySink sink = ResourceTelemetrySink.External(address, "otlp-headers");

        // Assert
        sink.IsExternal.ShouldBeTrue();
        sink.ExternalEndpoint.ShouldBe(address);
        sink.HeadersParameter.ShouldBe("otlp-headers");
        sink.Resource.ShouldBeNull();
        sink.EndpointName.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Telemetry sink: invalid arguments are rejected")]
    public void Factories_InvalidArguments_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => ResourceTelemetrySink.FromResource((IApplicationResourceDescriptor)null!));
        Should.Throw<ArgumentNullException>(() => ResourceTelemetrySink.External(null!));
        Should.Throw<ArgumentException>(() => ResourceTelemetrySink.External(new Uri("/relative", UriKind.Relative)));
        Should.Throw<ArgumentException>(() => ResourceTelemetrySink.External(new Uri("ftp://otel.example.test")));
        Should.Throw<ArgumentException>(() =>
            ResourceTelemetrySink.External(new Uri("https://otel.example.test:4318"), " "));
    }
}
