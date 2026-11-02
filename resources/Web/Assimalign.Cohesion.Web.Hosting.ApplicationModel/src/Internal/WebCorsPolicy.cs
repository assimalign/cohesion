using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

/// <summary>
/// The default CORS policy of a Web resource instance: the content the <c>cors</c> action reads and
/// writes, and the snapshot its gateway restores on new instances.
/// </summary>
/// <remarks>
/// <para>
/// An empty <see cref="Origins"/> list registers no default policy, so only endpoints with CORS
/// metadata of their own are governed. A list holding only <c>*</c> allows any origin, method, or
/// header. An empty <see cref="Methods"/> or <see cref="Headers"/> list keeps the CORS defaults.
/// </para>
/// <para>
/// The type is the resource's own: the gateway and its platform see only the content, camel-case
/// JSON written through <see cref="WebResourceJsonContext"/>:
/// <c>{ "origins": [ "https://shop.example" ], "methods": [ "GET", "POST" ], "allowCredentials": false }</c>.
/// </para>
/// </remarks>
internal sealed record WebCorsPolicy
{
    /// <summary>
    /// Gets the origins allowed to call the application, or <c>*</c> alone for any origin.
    /// </summary>
    public IReadOnlyList<string> Origins { get; init; } = [];

    /// <summary>
    /// Gets the methods a cross-origin request may use, or <c>*</c> alone for any method.
    /// </summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>
    /// Gets the request headers a cross-origin request may send, or <c>*</c> alone for any header.
    /// </summary>
    public IReadOnlyList<string> Headers { get; init; } = [];

    /// <summary>
    /// Gets the response headers a cross-origin caller may read.
    /// </summary>
    public IReadOnlyList<string> ExposedHeaders { get; init; } = [];

    /// <summary>
    /// Gets whether a cross-origin request may carry credentials.
    /// </summary>
    public bool AllowCredentials { get; init; }

    /// <summary>
    /// Gets how long, in seconds, a browser may cache a preflight answer; <see langword="null"/> leaves
    /// it to the browser.
    /// </summary>
    public int? PreflightMaxAgeSeconds { get; init; }
}
