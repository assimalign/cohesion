using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Represents a resource entry invocation that completed with a classified
/// <c>cohesion/sysexits/v1</c> exit code.
/// </summary>
public sealed class ResourceEntryExitException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceEntryExitException"/> class.
    /// </summary>
    /// <param name="exitCode">The classified resource entry exit code.</param>
    /// <param name="innerException">The failure that produced the exit code, when available.</param>
    public ResourceEntryExitException(int exitCode, Exception? innerException = null)
        : base($"Resource entry invocation exited with code {exitCode}.", innerException)
    {
        ExitCode = exitCode;
    }

    /// <summary>
    /// Gets the classified <c>cohesion/sysexits/v1</c> exit code.
    /// </summary>
    public int ExitCode { get; }
}
