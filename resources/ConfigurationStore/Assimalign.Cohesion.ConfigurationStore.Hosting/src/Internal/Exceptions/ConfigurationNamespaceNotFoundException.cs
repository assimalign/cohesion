using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

internal sealed class ConfigurationNamespaceNotFoundException : ResourceCommandRejectedException
{
    /// <summary>Initializes a new instance of the <see cref="ConfigurationNamespaceNotFoundException"/> class.</summary>
    /// <param name="detail">The named, actionable reason the namespace command was rejected.</param>
    public ConfigurationNamespaceNotFoundException(string detail)
        : base(detail)
    {
    }
}
