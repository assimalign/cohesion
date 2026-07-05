using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>The default <see cref="IOpenApiInfoBuilder"/> implementation.</summary>
internal sealed class OpenApiInfoBuilder : IOpenApiInfoBuilder
{
    private readonly OpenApiInfo _info;
    private readonly OpenApiSpecVersion _version;

    internal OpenApiInfoBuilder(OpenApiInfo info, OpenApiSpecVersion version)
    {
        _info = info;
        _version = version;
    }

    public IOpenApiInfoBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.InfoSummary, "The Info Object 'summary' field");
        _info.Summary = summary;
        return this;
    }

    public IOpenApiInfoBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _info.Description = description;
        return this;
    }

    public IOpenApiInfoBuilder TermsOfService(string termsOfService)
    {
        ArgumentNullException.ThrowIfNull(termsOfService);
        _info.TermsOfService = termsOfService;
        return this;
    }

    public IOpenApiInfoBuilder Contact(string? name = null, string? url = null, string? email = null)
    {
        _info.Contact = new OpenApiContact { Name = name, Url = url, Email = email };
        return this;
    }

    public IOpenApiInfoBuilder License(string name, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        _info.License = new OpenApiLicense { Name = name, Url = url };
        return this;
    }

    public IOpenApiInfoBuilder LicenseIdentifier(string name, string identifier)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(identifier);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.LicenseIdentifier, "The License Object 'identifier' field");
        _info.License = new OpenApiLicense { Name = name, Identifier = identifier };
        return this;
    }
}
