using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Inserts externally keyed documents from VALUES or a SELECT result.</summary>
public sealed class OqlInsertExpression : OqlExpression
{
    /// <summary>Initializes an INSERT statement.</summary>
    /// <param name="collection">The destination collection.</param>
    /// <param name="alias">The optional destination alias.</param>
    /// <param name="values">The key and value expressions for VALUES, in order.</param>
    /// <param name="select">The SELECT source, or null for VALUES.</param>
    /// <param name="keyName">The SELECT output name supplying the key.</param>
    /// <param name="valueName">The SELECT output name supplying the value, or null to store the output row.</param>
    /// <param name="location">The source span.</param>
    public OqlInsertExpression(string collection, string? alias,
        IReadOnlyList<(OqlExpression Key, OqlExpression Value)> values, OqlSelectExpression? select,
        string? keyName, string? valueName, Location? location = null) : base(location)
    {
        Collection = collection;
        Alias = alias;
        Values = values;
        Select = select;
        KeyName = keyName;
        ValueName = valueName;
    }

    /// <summary>Gets the destination collection.</summary>
    public string Collection { get; }
    /// <summary>Gets the optional destination alias.</summary>
    public string? Alias { get; }
    /// <summary>Gets the VALUES pairs in statement order.</summary>
    public IReadOnlyList<(OqlExpression Key, OqlExpression Value)> Values { get; }
    /// <summary>Gets the optional SELECT source.</summary>
    public OqlSelectExpression? Select { get; }
    /// <summary>Gets the SELECT key output name.</summary>
    public string? KeyName { get; }
    /// <summary>Gets the SELECT value output name, or null for the complete row.</summary>
    public string? ValueName { get; }
}
