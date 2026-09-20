using System;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// Options for configuring various behaviors of the default <see cref="IServiceProvider"/> implementation.
/// </summary>
public class ServiceProviderOptions
{
    // Avoid allocating objects in the default case
    internal static readonly ServiceProviderOptions Default = new();

    /// <summary>
    /// Gets or sets whether service resolvers may be compiled when the runtime supports dynamic code.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>true</c> for compatibility. When <c>false</c>, the provider uses only the
    /// interpreted runtime resolver and never schedules expression or IL compilation. This option
    /// does not make constructor-activation registrations reflection-free; use explicit factories
    /// or instances when reflection-free construction is required.
    /// </remarks>
    public bool EnableDynamicCode { get; set; } = true;

    /// <summary>
    /// <c>true</c> to perform check verifying that scoped services never gets resolved from root provider; otherwise <c>false</c>. Defaults to <c>false</c>.
    /// </summary>
    public bool ValidateScopes { get; set; }
    /// <summary>
    /// <c>true</c> to perform check verifying that all services can be created during <c>BuildServiceProvider</c> call; otherwise <c>false</c>. Defaults to <c>false</c>.
    /// NOTE: this check doesn't verify open generics services.
    /// </summary>
    public bool ValidateOnBuild { get; set; }
}
