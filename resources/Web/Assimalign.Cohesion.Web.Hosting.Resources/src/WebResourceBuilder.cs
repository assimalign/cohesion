using System;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Resources;

/// <summary>
/// Configures the Web resource that <c>builder.AddResource(name, ...)</c> adds: where it publishes
/// its manifest, where it serves its control plane, and the actions it exposes.
/// </summary>
public sealed class WebResourceBuilder
{
    private WebCorsAction? _cors;

    internal WebResourceBuilder(ResourceName name)
    {
        Name = name;
        ManifestEndpoint = ReadEndpoint(AppEnvironment.Variables.ManifestEndpoint);
        ControlPlaneEndpoint = ReadEndpoint(AppEnvironment.Variables.ControlPlaneEndpoint);
    }

    /// <summary>
    /// Gets the resource's name.
    /// </summary>
    public ResourceName Name { get; }

    /// <summary>
    /// Gets or sets the URL the instance publishes its manifest to; <see langword="null"/> runs the
    /// application unmanaged.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable the
    /// provisioning gateway sets.
    /// </remarks>
    public Uri? ManifestEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the URL the instance serves its control plane on, a private listener separate
    /// from the application's own. Required when <see cref="ManifestEndpoint"/> is set.
    /// </summary>
    /// <remarks>
    /// Defaults to the <see cref="AppEnvironment.Variables.ControlPlaneEndpoint"/> variable the
    /// provisioning gateway sets.
    /// </remarks>
    public Uri? ControlPlaneEndpoint { get; set; }

    /// <summary>
    /// Exposes the application's default CORS policy as the resource's <c>cors</c> action, starting
    /// from the policy the code defines. Serve it with <c>app.UseCors(resource)</c>.
    /// </summary>
    /// <param name="policy">The policy the code defines.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The policy names an invalid origin, method, or header.</exception>
    /// <exception cref="InvalidOperationException">
    /// A CORS policy was already added, or the policy combines settings CORS forbids, such as any
    /// origin with credentials.
    /// </exception>
    public WebResourceBuilder AddCors(WebCorsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (_cors is not null)
        {
            throw new InvalidOperationException(ErrorMessages.CorsAlreadyAdded);
        }

        _cors = new WebCorsAction(policy);
        return this;
    }

    internal WebResource Build()
    {
        return new WebResource(Name, _cors);
    }

    private static Uri? ReadEndpoint(string variable)
    {
        string? value = System.Environment.GetEnvironmentVariable(variable);

        return string.IsNullOrWhiteSpace(value) ? null : new Uri(value, UriKind.Absolute);
    }
}
