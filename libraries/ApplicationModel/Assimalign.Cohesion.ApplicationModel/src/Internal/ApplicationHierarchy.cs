using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

// What every set of one hierarchy shares: the gateway names in use, every resource by its gateway,
// provider, and name, and whether the application has been built.
internal sealed class ApplicationHierarchy
{
    public ApplicationHierarchy(GatewayName root)
    {
        GatewayNames = [root];
    }

    public HashSet<GatewayName> GatewayNames { get; }

    public Dictionary<(GatewayName Gateway, ProviderName Provider, ResourceName Name), ApplicationResourceDescriptor> Resources { get; } = [];

    public bool IsBuilt { get; private set; }

    public void MarkBuilt()
    {
        IsBuilt = true;
    }

    public void ThrowIfBuilt()
    {
        if (IsBuilt)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationCompositionClosed);
        }
    }
}
