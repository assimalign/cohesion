namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Identifies the supported action when a referenced parent row is deleted or its key is updated.</summary>
public enum SqlReferentialAction
{
    /// <summary>Refuses a parent change while children reference it.</summary>
    Restrict,
    /// <summary>Propagates a parent deletion or key change to referencing children.</summary>
    Cascade,
    /// <summary>Checks dependent rows at statement end.</summary>
    NoAction,
    /// <summary>Assigns NULL to the referencing columns.</summary>
    SetNull,
    /// <summary>Assigns each referencing column's default.</summary>
    SetDefault,
}
