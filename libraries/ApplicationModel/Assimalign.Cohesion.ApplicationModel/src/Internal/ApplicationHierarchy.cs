using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

// What every set of one hierarchy shares: the gateway names in use, and whether the application
// has been built.
internal sealed class ApplicationHierarchy
{
    public ApplicationHierarchy(GatewayName root)
    {
        GatewayNames = [root];
    }

    public HashSet<GatewayName> GatewayNames { get; }

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
