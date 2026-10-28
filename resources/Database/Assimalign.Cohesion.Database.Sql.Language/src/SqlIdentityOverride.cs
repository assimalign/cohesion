namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Controls explicit identity assignments in an INSERT.</summary>
public enum SqlIdentityOverride
{
    /// <summary>Use the column's declared generation policy.</summary>
    None,
    /// <summary>Permit explicit values for ALWAYS identities.</summary>
    System,
    /// <summary>Generate identity values, ignoring supplied values.</summary>
    User,
}
