namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for the <see cref="OpenApiInfo"/> object.
/// </summary>
public interface IOpenApiInfoBuilder
{
    /// <summary>Sets a short summary of the API (OpenAPI 3.1+).</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiInfoBuilder Summary(string summary);

    /// <summary>Sets a description of the API.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiInfoBuilder Description(string description);

    /// <summary>Sets the Terms of Service URI.</summary>
    /// <param name="termsOfService">The Terms of Service URI.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiInfoBuilder TermsOfService(string termsOfService);

    /// <summary>Sets the contact information.</summary>
    /// <param name="name">The contact name.</param>
    /// <param name="url">The contact URL.</param>
    /// <param name="email">The contact email.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiInfoBuilder Contact(string? name = null, string? url = null, string? email = null);

    /// <summary>Sets the license by name and optional URL.</summary>
    /// <param name="name">The license name.</param>
    /// <param name="url">The license URL.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiInfoBuilder License(string name, string? url = null);

    /// <summary>Sets the license by SPDX identifier (OpenAPI 3.1+).</summary>
    /// <param name="name">The license name.</param>
    /// <param name="identifier">The SPDX license identifier.</param>
    /// <returns>The same builder for chaining.</returns>
    IOpenApiInfoBuilder LicenseIdentifier(string name, string identifier);
}
