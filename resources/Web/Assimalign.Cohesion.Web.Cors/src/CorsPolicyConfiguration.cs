namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Describes a CORS policy using values that can be exchanged as JSON and validated by
/// <see cref="CorsPolicyStore"/>.
/// </summary>
/// <remarks>
/// The configuration supports explicit origins and the any-origin option. Executable origin predicates
/// remain available through the static <see cref="CorsPolicyBuilder"/> API only.
/// </remarks>
public sealed class CorsPolicyConfiguration
{
    /// <summary>Gets or sets whether every origin may read responses.</summary>
    public bool AllowAnyOrigin { get; set; }

    /// <summary>Gets or sets the allowed origins, normalized when the configuration is published.</summary>
    public string[] Origins { get; set; } = [];

    /// <summary>Gets or sets whether every method may be approved by a preflight.</summary>
    public bool AllowAnyMethod { get; set; }

    /// <summary>Gets or sets allowed methods in addition to the CORS-safelisted methods.</summary>
    public string[] Methods { get; set; } = [];

    /// <summary>Gets or sets whether every request header may be approved by a preflight.</summary>
    public bool AllowAnyHeader { get; set; }

    /// <summary>Gets or sets the allowed request-header names.</summary>
    public string[] Headers { get; set; } = [];

    /// <summary>Gets or sets the response-header names exposed to the calling script.</summary>
    public string[] ExposedHeaders { get; set; } = [];

    /// <summary>Gets or sets whether responses may be shared with credentialed requests.</summary>
    public bool AllowCredentials { get; set; }

    /// <summary>
    /// Gets or sets the preflight cache lifetime in whole seconds; <see langword="null"/> leaves it to
    /// the browser's default. Zero disables caching.
    /// </summary>
    public long? PreflightMaxAgeSeconds { get; set; }
}
