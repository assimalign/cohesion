using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiExampleBuilder"/> implementation.</summary>
internal sealed class OpenApiExampleBuilder : IOpenApiExampleBuilder
{
    private readonly OpenApiExample _example = new();
    private readonly OpenApiSpecVersion _version;

    internal OpenApiExampleBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    internal OpenApiExample Build() => _example;

    public IOpenApiExampleBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _example.Summary = summary;
        return this;
    }

    public IOpenApiExampleBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _example.Description = description;
        return this;
    }

    public IOpenApiExampleBuilder Value(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _example.Value = value;
        return this;
    }

    public IOpenApiExampleBuilder DataValue(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.ExampleDataAndSerializedValues, "The Example Object 'dataValue' field");
        _example.DataValue = value;
        return this;
    }

    public IOpenApiExampleBuilder SerializedValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.ExampleDataAndSerializedValues, "The Example Object 'serializedValue' field");
        _example.SerializedValue = value;
        return this;
    }

    public IOpenApiExampleBuilder ExternalValue(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        _example.ExternalValue = uri;
        return this;
    }
}
