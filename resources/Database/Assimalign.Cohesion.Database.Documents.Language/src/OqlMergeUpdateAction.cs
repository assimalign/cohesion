using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The matched UPDATE action, reusing ordinary SET and UNSET paths.</summary>
public sealed record OqlMergeUpdateAction
{
    /// <summary>Initializes an UPDATE action with snapshots of its mutation lists.</summary>
    /// <param name="assignments">The ordered SET assignments.</param>
    /// <param name="unset">The ordered UNSET paths.</param>
    /// <param name="predicate">The optional action WHERE expression.</param>
    /// <exception cref="ArgumentNullException">A list or one of its elements is null.</exception>
    public OqlMergeUpdateAction(IReadOnlyList<OqlUpdateAssignment> assignments,
        IReadOnlyList<OqlPathExpression> unset, OqlExpression? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(unset);
        var assignmentSnapshot = assignments.ToArray();
        var unsetSnapshot = unset.ToArray();
        foreach (var assignment in assignmentSnapshot) { ArgumentNullException.ThrowIfNull(assignment, nameof(assignments)); }
        foreach (var path in unsetSnapshot) { ArgumentNullException.ThrowIfNull(path, nameof(unset)); }
        Assignments = Array.AsReadOnly(assignmentSnapshot); Unset = Array.AsReadOnly(unsetSnapshot); Predicate = predicate;
    }

    /// <summary>Gets the read-only snapshot of SET assignments.</summary>
    public IReadOnlyList<OqlUpdateAssignment> Assignments { get; }
    /// <summary>Gets the read-only snapshot of UNSET paths.</summary>
    public IReadOnlyList<OqlPathExpression> Unset { get; }
    /// <summary>Gets the optional action filter.</summary>
    public OqlExpression? Predicate { get; }
}
