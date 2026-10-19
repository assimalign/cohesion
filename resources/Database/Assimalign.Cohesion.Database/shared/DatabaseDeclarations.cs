using System;
using System.Collections.Generic;
using System.Threading;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The one wording, and the one record, of the databases an engine's builder declared (B3 of the
/// engine extensibility design; database-area.md rule 8): the duplicate refused at the declaration,
/// and the drop the declaration refuses on the built engine (owner decision 56 of 2026-10-09).
/// </summary>
/// <remarks>
/// Compiled into each model assembly from the root's <c>shared</c> folder, as the builder state is:
/// each model's engine records its declarations in a field of its own and refuses the drop in its
/// own drop core, inside the root base's traced drop, so the refusal is traced like any failed drop.
/// The SQL engine records richer declarations (a schema and a collation each) and refuses through
/// the same message.
/// </remarks>
internal static class DatabaseDeclarations
{
    /// <summary>
    /// Creates the refusal of a database declared twice on one engine builder.
    /// </summary>
    /// <param name="model">The model's name as its messages start (<c>SQL</c>, <c>Key-value</c>, …).</param>
    /// <param name="engineName">The engine name.</param>
    /// <param name="declared">The database as it was first declared.</param>
    /// <returns>The exception to throw.</returns>
    internal static InvalidOperationException AlreadyDeclared(string model, string engineName, DatabaseName declared)
        => new($"{model} engine '{engineName}' already declares database '{declared}'.");

    /// <summary>
    /// Creates the refusal of a drop of a database the engine's builder declared: the declaration
    /// owns the database at the database level, so it leaves only when the declaration does.
    /// </summary>
    /// <param name="model">The model's name as its messages start (<c>SQL</c>, <c>Key-value</c>, …).</param>
    /// <param name="engineName">The engine name.</param>
    /// <param name="declared">The database as it was declared.</param>
    /// <param name="builderType">The model's engine builder type name, for the remedy.</param>
    /// <returns>The exception to throw.</returns>
    internal static DatabaseObjectLockedException RefuseDrop(string model, string engineName, DatabaseName declared, string builderType)
        => new(
            declared,
            declared,
            "DROP DATABASE",
            $"{model} engine '{engineName}' declares database '{declared}' ({builderType}.AddDatabase), so " +
            "DROP DATABASE is refused. Remove the declaration from the engine builder and rebuild the engine before dropping it.");

    /// <summary>
    /// Records an engine's declared databases once, before they are opened: from then on the
    /// engine refuses to drop each of them.
    /// </summary>
    /// <param name="field">The engine's field, empty until this call.</param>
    /// <param name="declarations">The declared databases, in declaration order.</param>
    /// <param name="engineName">The engine name, for the message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="declarations"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The engine already declares its databases.</exception>
    internal static void Declare(ref DatabaseName[] field, DatabaseName[] declarations, string engineName)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var none = field;
        if (none.Length != 0 || !ReferenceEquals(Interlocked.CompareExchange(ref field, declarations, none), none))
        {
            throw new InvalidOperationException($"Engine '{engineName}' already declares its databases.");
        }
    }

    /// <summary>
    /// Finds a database among an engine's declared databases.
    /// </summary>
    /// <param name="declarations">The engine's declared databases.</param>
    /// <param name="name">The database name; database names compare ignoring case.</param>
    /// <param name="declared">When this method returns true, the database as it was declared.</param>
    /// <returns>True when the engine's builder declared the database.</returns>
    internal static bool TryFind(IReadOnlyList<DatabaseName> declarations, DatabaseName name, out DatabaseName declared)
    {
        foreach (var candidate in declarations)
        {
            if (candidate == name)
            {
                declared = candidate;
                return true;
            }
        }

        declared = default;
        return false;
    }
}
