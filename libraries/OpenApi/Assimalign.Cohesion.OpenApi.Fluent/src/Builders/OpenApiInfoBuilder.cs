using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for the <see cref="OpenApiInfo"/> object.
/// </summary>
public sealed class OpenApiInfoBuilder
{
    private readonly OpenApiInfo _info;
    private readonly OpenApiSpecVersion _version;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiInfoBuilder"/> class.
    /// </summary>
    /// <param name="info">The <see cref="OpenApiInfo"/> being configured.</param>
    /// <param name="version">The target OpenAPI specification version.</param>
    public OpenApiInfoBuilder(OpenApiInfo info, OpenApiSpecVersion version)
    {
        _info = info;
        _version = version;
    }

    /// <summary>Sets a short summary of the API (OpenAPI 3.1+).</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiInfoBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.InfoSummary, "The Info Object 'summary' field");
        _info.Summary = summary;
        return this;
    }

    /// <summary>Sets a description of the API.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiInfoBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _info.Description = description;
        return this;
    }

    /// <summary>Sets the Terms of Service URI.</summary>
    /// <param name="termsOfService">The Terms of Service URI.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiInfoBuilder TermsOfService(string termsOfService)
    {
        ArgumentNullException.ThrowIfNull(termsOfService);
        _info.TermsOfService = termsOfService;
        return this;
    }

    /// <summary>Sets the contact information.</summary>
    /// <param name="name">The contact name.</param>
    /// <param name="url">The contact URL.</param>
    /// <param name="email">The contact email.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiInfoBuilder Contact(string? name = null, string? url = null, string? email = null)
    {
        _info.Contact = new OpenApiContact { Name = name, Url = url, Email = email };
        return this;
    }

    /// <summary>Sets the license by name and optional URL.</summary>
    /// <param name="name">The license name.</param>
    /// <param name="url">The license URL.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiInfoBuilder License(string name, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        _info.License = new OpenApiLicense { Name = name, Url = url };
        return this;
    }

    /// <summary>Sets the license by SPDX identifier (OpenAPI 3.1+).</summary>
    /// <param name="name">The license name.</param>
    /// <param name="identifier">The SPDX license identifier.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiInfoBuilder LicenseIdentifier(string name, string identifier)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(identifier);
        OpenApiBuildGuard.Require(_version, OpenApiFeature.LicenseIdentifier, "The License Object 'identifier' field");
        _info.License = new OpenApiLicense { Name = name, Identifier = identifier };
        return this;
    }
}
