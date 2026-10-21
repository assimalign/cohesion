namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Identifies the supported action when a referenced parent row is deleted.</summary>
public enum SqlReferentialAction
{
    /// <summary>Refuse deletion while children reference the parent.</summary>
    Restrict,
    /// <summary>Delete referencing children with their parent.</summary>
    Cascade,
    /// <summary>Checks dependent rows at statement end.</summary>
    NoAction,
    /// <summary>Assigns NULL to the referencing columns.</summary>
    SetNull,
    /// <summary>Assigns each referencing column's default.</summary>
    SetDefault,
}
