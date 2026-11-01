using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

/// <summary>
/// The program a resource runs: the entry point the gateway program calls for it.
/// </summary>
internal sealed record InProcessProgram(ProviderName Provider, ResourceName Name, Func<string[], Task> Main)
{
    /// <summary>
    /// Creates a binding after checking each part of it, as every place that binds a program does.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="main"/> is <see langword="null"/>.</exception>
    public static InProcessProgram Create(ProviderName provider, ResourceName name, Func<string[], Task> main)
    {
        if (provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(provider));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
        }

        ArgumentNullException.ThrowIfNull(main);
        return new InProcessProgram(provider, name, main);
    }
}
