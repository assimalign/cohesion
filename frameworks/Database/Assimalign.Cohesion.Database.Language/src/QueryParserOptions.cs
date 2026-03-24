using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Language;

public class QueryParserOptions
{
    /// <summary>
    /// 
    /// </summary>
    public List<QueryAnalyzer> Analyzers { get; } = new List<QueryAnalyzer>();

    /// <summary>
    /// Specify the timeout for query analysis. Default is 5 seconds.
    /// </summary>
    public TimeSpan AnalyzerTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
