using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>Configures the analyzers run after parsing and their shared completion timeout.</summary>
/// <remarks>Model options may derive from this non-virtual configuration carrier; SQL adds its expression nesting limit.</remarks>
public class QueryParserOptions
{
    /// <summary>
    /// Gets the analyzers invoked in registration order after a statement is parsed.
    /// </summary>
    public List<QueryAnalyzer> Analyzers { get; } = new List<QueryAnalyzer>();

    /// <summary>
    /// Gets or sets the timeout shared by all analyzers. Defaults to five seconds; ignored when no analyzers are registered.
    /// </summary>
    public TimeSpan AnalyzerTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
