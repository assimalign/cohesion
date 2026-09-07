using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Common options for plan-driven application gateways.
/// </summary>
public class ApplicationGatewayOptions
{
    /// <summary>
    /// Gets domain-authored controller overrides. Controllers are consulted in registration
    /// order before the platform gateway's built-in plan controller. The framework registers
    /// no overrides by default.
    /// </summary>
    public IList<IApplicationResourceController> Controllers { get; } =
        new List<IApplicationResourceController>();

    /// <summary>
    /// Gets parameter values available to the default <c>parameter:</c> mount-source
    /// resolver. Values are encoded as UTF-8 for file delivery.
    /// </summary>
    public IDictionary<string, string> Parameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the readiness budget applied independently to each resource. Defaults
    /// to 60&#160;seconds.
    /// </summary>
    public TimeSpan ReadinessBudget { get; set; } = TimeSpan.FromSeconds(60);

    internal void ValidateCommon()
    {
        if (ReadinessBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReadinessBudget),
                "ReadinessBudget must be greater than zero.");
        }

        for (int index = 0; index < Controllers.Count; index++)
        {
            if (Controllers[index] is null)
            {
                throw new ArgumentException(
                    $"Controller registration at index {index} is null.",
                    nameof(Controllers));
            }
        }
    }
}
