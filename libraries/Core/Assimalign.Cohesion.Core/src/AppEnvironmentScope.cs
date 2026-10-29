using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion;

/// <summary>
/// An environment of its own for a program that shares its process with other programs: the
/// variables it reads and the shutdown signal it stops on.
/// </summary>
/// <remarks>
/// <para>
/// A process gives each program in it one set of environment variables and one set of shutdown
/// signals. A host of programs, such as a gateway that runs its resources in its own process, gives
/// each program a scope instead. It enters the scope (<see cref="AppEnvironment.Enter"/>) on the flow
/// it runs the program on. The program, and the work it starts, then read the scope's variables
/// through <see cref="AppEnvironment"/>, and a host it runs stops when <see cref="Shutdown"/> is
/// signaled, not on the process's signals.
/// </para>
/// <para>
/// The runtime contract (<see cref="AppEnvironment.Variables"/>) belongs to the scope alone. Inside
/// it, a contract variable the scope does not set reads as unset, whatever the process sets, so
/// one program never reads another's gateway, endpoints, or configuration. Every other variable,
/// such as <c>PATH</c>, reads through to the process.
/// </para>
/// </remarks>
[DebuggerDisplay("Variables = {Variables.Count}")]
public sealed class AppEnvironmentScope
{
    private AppEnvironmentScope(FrozenDictionary<string, string> variables, CancellationToken shutdown)
    {
        Variables = variables;
        Shutdown = shutdown;
    }

    /// <summary>
    /// Gets the variables the scope sets, by name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>
    /// Gets the signal a host run inside the scope stops on, in place of the process's shutdown
    /// signals.
    /// </summary>
    public CancellationToken Shutdown { get; }

    /// <summary>
    /// Creates a scope.
    /// </summary>
    /// <param name="variables">The variables the scope sets, by name. They are copied.</param>
    /// <param name="shutdown">The signal a host run inside the scope stops on.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is <see langword="null"/>, or holds a <see langword="null"/> value.</exception>
    /// <exception cref="ArgumentException">A variable's name is empty or white space.</exception>
    public static AppEnvironmentScope Create(IReadOnlyDictionary<string, string> variables, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(variables);

        foreach (KeyValuePair<string, string> variable in variables)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(variable.Key, nameof(variables));
            ArgumentNullException.ThrowIfNull(variable.Value, nameof(variables));
        }

        return new AppEnvironmentScope(variables.ToFrozenDictionary(StringComparer.Ordinal), shutdown);
    }
}
