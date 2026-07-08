using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// The outcome of decoding a QPACK request field section: the decoded field
/// lines and whether the section referenced the dynamic table (RFC 9204
/// §4.4.1 — a referencing section is owed a Section Acknowledgment).
/// </summary>
internal readonly struct QPackDecodeResult
{
    /// <summary>
    /// Initializes a new <see cref="QPackDecodeResult"/>.
    /// </summary>
    /// <param name="fields">The decoded field lines, in wire order.</param>
    /// <param name="referencedDynamicTable">Whether the section referenced the dynamic table.</param>
    public QPackDecodeResult(List<(string Name, string Value)> fields, bool referencedDynamicTable)
    {
        Fields = fields;
        ReferencedDynamicTable = referencedDynamicTable;
    }

    /// <summary>Gets the decoded field lines, in wire order.</summary>
    public List<(string Name, string Value)> Fields { get; }

    /// <summary>
    /// Gets whether the field section referenced the dynamic table (non-zero
    /// Required Insert Count), meaning a Section Acknowledgment is owed.
    /// </summary>
    public bool ReferencedDynamicTable { get; }
}
