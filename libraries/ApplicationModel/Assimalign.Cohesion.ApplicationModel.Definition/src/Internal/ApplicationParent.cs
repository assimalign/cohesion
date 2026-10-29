using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[DebuggerDisplay("{Name}")]
internal sealed class ApplicationParent : IApplicationParent
{
    public ApplicationParent(GatewayName name)
    {
        Name = name;
    }

    public GatewayName Name { get; }

    public ApplicationResourceOutput Output(ProviderName provider, ResourceName name, OutputName output)
    {
        if (provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(provider));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
        }

        if (output.Value is null)
        {
            throw new ArgumentException(ErrorMessages.OutputNameMissing, nameof(output));
        }

        return new ApplicationResourceOutput(Name, provider, name, output);
    }
}
