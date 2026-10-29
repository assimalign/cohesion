namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Members removed from an otherwise complete window frame.</summary>
public enum SqlWindowExclusion
{
    /// <summary>Keep every frame member (the default).</summary>
    NoOthers,
    /// <summary>Remove the current row.</summary>
    CurrentRow,
    /// <summary>Remove the current row and all its ordering peers.</summary>
    Group,
    /// <summary>Remove the current row's peers, retaining the current row when in the frame.</summary>
    Ties,
}
