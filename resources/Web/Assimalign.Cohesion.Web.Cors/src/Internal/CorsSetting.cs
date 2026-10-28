using System;
using System.Collections.Frozen;
using System.Linq;
using System.Threading;

namespace Assimalign.Cohesion.Web.Cors.Internal;

/// <summary>
/// The application's <c>cors</c> setting: the default policy, which an operator surface may replace
/// while the application runs, and the named policies, fixed when <c>AddCors</c> registered them.
/// </summary>
internal sealed class CorsSetting : IWebApplicationSetting<CorsPolicy?>
{
    /// <summary>
    /// The setting's name.
    /// </summary>
    public const string SettingName = "cors";

    private CorsPolicy? _defaultPolicy;

    private CorsSetting(CorsPolicy? defaultPolicy, FrozenDictionary<string, CorsPolicy> policies)
    {
        _defaultPolicy = defaultPolicy;
        Policies = policies;
    }

    public string Name => SettingName;

    public CorsPolicy? Value => Volatile.Read(ref _defaultPolicy);

    /// <summary>
    /// Gets the named policies endpoints reference through <see cref="CorsMetadata"/>.
    /// </summary>
    public FrozenDictionary<string, CorsPolicy> Policies { get; }

    public void Set(CorsPolicy? value)
    {
        Volatile.Write(ref _defaultPolicy, value);
    }

    /// <summary>
    /// Creates the setting from a snapshot of the options, so changing them afterwards has no effect.
    /// </summary>
    public static CorsSetting Create(CorsOptions options)
    {
        return new CorsSetting(options.DefaultPolicy, options.Policies.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>
    /// Finds the setting <c>AddCors</c> registered last. Without one, the middleware governs only the
    /// endpoints that carry an inline policy.
    /// </summary>
    public static CorsSetting Find(IWebApplicationContext context)
    {
        return context.Settings.OfType<CorsSetting>().LastOrDefault()
            ?? new CorsSetting(defaultPolicy: null, FrozenDictionary<string, CorsPolicy>.Empty);
    }
}
