using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Language;

public class QueryAnalyzerContext
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
