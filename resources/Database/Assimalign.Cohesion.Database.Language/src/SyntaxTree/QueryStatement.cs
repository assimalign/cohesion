using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>A parsed model statement with diagnostics that analyzers may add concurrently.</summary>
public abstract class QueryStatement
{
    private readonly ConcurrentBag<Diagnostic> _diagnostics;

    /// <summary>Initializes a statement with an empty diagnostic collection.</summary>
    protected QueryStatement()
    {
        _diagnostics = new ConcurrentBag<Diagnostic>();
    }

    /// <summary>
    /// Gets the root expression of this statement.
    /// </summary>
    public abstract QueryExpression Expression { get; }


    /// <summary>Gets the diagnostics ordered by severity, then by source end position.</summary>
    public IEnumerable<Diagnostic> Diagnostics => _diagnostics.OrderBy(x => x.Severity).ThenBy(x => x.End);



    /// <summary>Adds a diagnostic to the statement.</summary>
    /// <param name="diagnostic">The diagnostic to report.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is null.</exception>
    public virtual void AddDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _diagnostics.Add(diagnostic);
    }
}
