using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>Provides the parsed statement on which an analyzer can report diagnostics.</summary>
public sealed class QueryAnalyzerContext
{
    internal QueryAnalyzerContext(QueryStatement document)
    {
        Statement = document;
    }

    /// <summary>
    /// The parsed query statement.
    /// </summary>
    public QueryStatement Statement { get; }
}
