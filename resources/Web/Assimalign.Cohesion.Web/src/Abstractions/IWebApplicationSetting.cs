namespace Assimalign.Cohesion.Web;

/// <summary>
/// A named value the application's components read while it serves, which can be replaced without
/// rebuilding the application.
/// </summary>
/// <remarks>
/// <para>
/// A setting is configured where it is registered: a feature package's
/// <c>builder.Services.Add&lt;Feature&gt;(...)</c> verb, or
/// <see cref="IWebApplicationBuilder.AddSetting(IWebApplicationSetting)"/> for one no package
/// registers. A pipeline verb only reads it; it never configures it.
/// </para>
/// <para>
/// The application lists its settings on <see cref="IWebApplicationContext.Settings"/>, where an
/// operator surface, such as a resource's control plane, finds them and replaces their values while
/// the application runs.
/// </para>
/// </remarks>
public interface IWebApplicationSetting
{
    /// <summary>
    /// Gets the setting's name, unique within the application, such as <c>cors</c>.
    /// </summary>
    string Name { get; }
}
