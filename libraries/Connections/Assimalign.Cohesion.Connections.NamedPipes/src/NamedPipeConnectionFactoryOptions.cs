using System;
using System.Security.Principal;

namespace Assimalign.Cohesion.Connections.NamedPipes;

/// <summary>
/// Provides pipe-tuning options for <see cref="NamedPipeConnectionFactory"/>.
/// </summary>
public sealed class NamedPipeConnectionFactoryOptions
{
    /// <summary>
    /// Gets or sets whether the client pipe is opened in write-through mode, so a write does not return
    /// until it has been transmitted to the other end. Defaults to <see langword="false"/>.
    /// </summary>
    public bool WriteThrough { get; set; }

    /// <summary>
    /// Gets or sets the security impersonation level the server may use when impersonating the client.
    /// Defaults to <see cref="TokenImpersonationLevel.None"/>.
    /// </summary>
    public TokenImpersonationLevel ImpersonationLevel { get; set; } = TokenImpersonationLevel.None;

    /// <summary>
    /// The default options.
    /// </summary>
    public static NamedPipeConnectionFactoryOptions Default { get; } = new();

    /// <summary>
    /// Creates a new <see cref="NamedPipeConnectionFactoryOptions"/> configured by the supplied delegate.
    /// </summary>
    /// <param name="configure">A delegate used to configure the options.</param>
    /// <returns>The configured options.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static NamedPipeConnectionFactoryOptions Create(Action<NamedPipeConnectionFactoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        NamedPipeConnectionFactoryOptions options = new();
        configure(options);

        return options;
    }
}
