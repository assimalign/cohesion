namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The orientation of a relationship relative to its pattern's node order.</summary>
/// <remarks>
/// ISO/IEC 39075 16.7 spells each form in full and abbreviated: <c>-[]-&gt;</c> or <c>-&gt;</c>,
/// <c>&lt;-[]-</c> or <c>&lt;-</c>, <c>-[]-</c> or <c>-</c>, and <c>&lt;-[]-&gt;</c> or
/// <c>&lt;-&gt;</c>. The engine stores only directed relationships, so the ISO undirected (tilde)
/// forms are not accepted, and <see cref="Undirected"/> and <see cref="LeftOrRight"/> match the
/// same relationships.
/// </remarks>
public enum GqlPatternDirection
{
    /// <summary>The relationship points from the preceding node to the following node: <c>-[]-&gt;</c> or <c>-&gt;</c>.</summary>
    Outgoing,
    /// <summary>The relationship points from the following node to the preceding node: <c>&lt;-[]-</c> or <c>&lt;-</c>.</summary>
    Incoming,
    /// <summary>Either orientation is accepted (ISO any direction): <c>-[]-</c> or <c>-</c>.</summary>
    Undirected,
    /// <summary>
    /// Either orientation is accepted (ISO left or right): <c>&lt;-[]-&gt;</c> or <c>&lt;-&gt;</c>.
    /// It matches as <see cref="Undirected"/> does, and insertion rejects it as it rejects
    /// <see cref="Undirected"/>, because an inserted relationship needs one direction.
    /// </summary>
    LeftOrRight,
}
