using System;

namespace Assimalign.Cohesion.Cli.Internal;

internal sealed class CliException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CliException"/> class.</summary>
    /// <param name="message">The user-facing message that describes the error.</param>
    public CliException(string message) : base(message)
    {
    }
}
