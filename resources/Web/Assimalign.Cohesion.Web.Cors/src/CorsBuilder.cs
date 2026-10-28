using Assimalign.Cohesion.Web.Cors.Internal;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Configures the CORS policies that <c>builder.Services.AddCors(...)</c> registers and
/// <c>UseCors()</c> applies.
/// </summary>
/// <remarks>
/// The builder registers the application's <c>cors</c> setting, an
/// <see cref="IWebApplicationSetting{TValue}"/> of <see cref="CorsPolicy"/> on
/// <see cref="IWebApplicationContext.Settings"/>. Its value is the default policy, which an operator
/// surface, such as a resource's control plane, may replace while the application runs; the
/// middleware reads it on each request. The named policies are fixed when the setting is built.
/// </remarks>
public sealed class CorsBuilder
{
    /// <summary>
    /// Gets the options the builder registers: the default policy and the named policies.
    /// </summary>
    public CorsOptions Options { get; } = new();

    /// <summary>
    /// Builds the application's <c>cors</c> setting from a snapshot of <see cref="Options"/>.
    /// </summary>
    /// <returns>The setting, whose value is the default policy, or <see langword="null"/> when none was added.</returns>
    public IWebApplicationSetting<CorsPolicy?> Build()
    {
        return CorsSetting.Create(Options);
    }
}
