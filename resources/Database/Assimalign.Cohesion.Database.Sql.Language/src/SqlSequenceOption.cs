namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>One explicit sequence option. A null value spells NO MINVALUE, NO MAXVALUE or bare RESTART.</summary>
public sealed class SqlSequenceOption
{
    internal SqlSequenceOption(string name, long? value = null, string? typeName = null, SqlTableReference? ownerTable = null, string? ownerColumn = null)
    { Name = name; Value = value; TypeName = typeName; OwnerTable = ownerTable; OwnerColumn = ownerColumn; }

    /// <summary>Gets the normalized option name.</summary>
    public string Name { get; }

    /// <summary>Gets the integer value, including 1/0 for CYCLE/NO CYCLE.</summary>
    public long? Value { get; }

    /// <summary>Gets the AS type spelling, when present.</summary>
    public string? TypeName { get; }
    /// <summary>Gets the OWNED BY table, or null for NONE.</summary>
    public SqlTableReference? OwnerTable { get; }
    /// <summary>Gets the OWNED BY column, or null for NONE.</summary>
    public string? OwnerColumn { get; }
}
