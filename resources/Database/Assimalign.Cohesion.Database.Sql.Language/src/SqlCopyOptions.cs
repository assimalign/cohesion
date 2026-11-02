using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>The resolved options of a text or CSV COPY TO export. Encoding is always UTF-8.</summary>
public sealed class SqlCopyOptions
{
    internal SqlCopyOptions(bool csv, char delimiter, string nullString, bool header, char quote, char escape,
        bool forceQuoteAll, IReadOnlyList<string> forceQuote)
    {
        IsCsv = csv;
        Delimiter = delimiter;
        NullString = nullString;
        Header = header;
        Quote = quote;
        Escape = escape;
        ForceQuoteAll = forceQuoteAll;
        ForceQuote = Array.AsReadOnly(forceQuote.ToArray());
    }

    /// <summary>Gets whether CSV is selected; false selects text.</summary>
    public bool IsCsv { get; }
    /// <summary>Gets the single ASCII field delimiter.</summary>
    public char Delimiter { get; }
    /// <summary>Gets the unquoted NULL representation.</summary>
    public string NullString { get; }
    /// <summary>Gets whether a column-name header precedes the data.</summary>
    public bool Header { get; }
    /// <summary>Gets the CSV quote character.</summary>
    public char Quote { get; }
    /// <summary>Gets the CSV quote escape character.</summary>
    public char Escape { get; }
    /// <summary>Gets whether every non-NULL CSV field is quoted.</summary>
    public bool ForceQuoteAll { get; }
    /// <summary>Gets the selected output columns whose non-NULL CSV fields are always quoted.</summary>
    public IReadOnlyList<string> ForceQuote { get; }
}
