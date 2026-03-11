using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language;

public abstract class QueryStatement
{
    private readonly ConcurrentBag<Diagnostic> _diagnostics;

    protected QueryStatement()
    {
        _diagnostics = new ConcurrentBag<Diagnostic>();
    }

    /// <summary>
    /// 
    /// </summary>
    public abstract QueryExpression Expression { get; }


    public IEnumerable<Diagnostic> Diagnostics => _diagnostics.OrderBy(x => x.Severity).ThenBy(x => x.End);



    public virtual void AddDiagnostic(Diagnostic diagnostic)
    {
        if (_diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostic));
        }
        _diagnostics.Add(diagnostic);
    }
}
