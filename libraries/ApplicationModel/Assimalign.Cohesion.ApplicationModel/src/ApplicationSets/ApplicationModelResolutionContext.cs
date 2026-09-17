using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies the current gateway invocation while a member application is described.
/// </summary>
public sealed class ApplicationModelResolutionContext
{
    /// <summary>Initializes a model-resolution context.</summary>
    /// <param name="environment">The current target environment.</param>
    /// <param name="runMode">The current gateway operation.</param>
    /// <param name="gatewayIdentity">The one gateway that will own all resolved models.</param>
    /// <param name="realize">The external resource names requested for Local realization.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    public ApplicationModelResolutionContext(
        IApplicationEnvironment environment,
        GatewayRunMode runMode,
        ResourceName gatewayIdentity,
        IReadOnlyList<ResourceName>? realize = null)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        RunMode = runMode;
        GatewayIdentity = gatewayIdentity;
        IReadOnlyList<ResourceName> requested = realize ?? Array.Empty<ResourceName>();
        var realizeCopy = new ResourceName[requested.Count];
        for (int index = 0; index < realizeCopy.Length; index++)
        {
            realizeCopy[index] = requested[index];
        }

        Realize = new ReadOnlyCollection<ResourceName>(realizeCopy);
    }

    /// <summary>Gets the target environment.</summary>
    public IApplicationEnvironment Environment { get; }

    /// <summary>Gets the selected gateway operation.</summary>
    public GatewayRunMode RunMode { get; }

    /// <summary>Gets the shared gateway identity.</summary>
    public ResourceName GatewayIdentity { get; }

    /// <summary>Gets the external resources requested for Local realization.</summary>
    public IReadOnlyList<ResourceName> Realize { get; }
}
