using System;
using System.IO;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The name rule every model applies to a database it stores: a database's files live in a
/// directory named for it under the engine's root path, so its name must be a single file-name
/// component. Without the rule, a name such as <c>../other</c> or an absolute path would create,
/// open or drop files outside the root.
/// </summary>
/// <remarks>
/// Compiled into each model assembly from the root's <c>shared</c> folder, as
/// <see cref="DatabaseDeclarations"/> is. Each engine checks it in its create, open and drop cores,
/// after the root base's checks of an empty name, disposal and the token, so the guard order of the
/// public members is unchanged; each engine builder also checks it when a database is declared, so a
/// declaration the engine would refuse fails at the call, before anything is created. The rule is the
/// one the Graph, Documents and Blob engines always applied, now shared with SQL and key-value.
/// </remarks>
internal static class DatabaseFileNames
{
    /// <summary>
    /// The refusal's message, the same on every model.
    /// </summary>
    internal const string NotSingleComponent = "A database name must be a single file-name component.";

    /// <summary>
    /// Refuses a database name that is blank, or that is not a single file-name component: <c>.</c>,
    /// <c>..</c>, a name holding a directory separator, or a name holding a character the platform
    /// does not allow in a file name.
    /// </summary>
    /// <param name="name">The database name.</param>
    /// <param name="paramName">The parameter the name was passed as.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space, or not a single file-name component.
    /// </exception>
    internal static void ThrowIfNotSingleComponent(string name, string paramName = "name")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            throw new ArgumentException(NotSingleComponent, paramName);
        }
    }
}
