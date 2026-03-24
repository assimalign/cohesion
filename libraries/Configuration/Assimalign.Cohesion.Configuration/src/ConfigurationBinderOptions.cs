namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Controls how configuration values are bound into object graphs.
/// </summary>
public class ConfigurationBinderOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the binder should write to non-public settable properties.
    /// </summary>
    public bool BindNonPublicProperties { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the binder should throw when configuration contains keys
    /// that do not map to public bindable properties on the target type.
    /// </summary>
    public bool ErrorOnUnknownConfiguration { get; set; }
}
