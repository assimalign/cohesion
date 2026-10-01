using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Base class for all SQL scalar expressions that can appear in WHERE, HAVING,
/// SET, SELECT lists, and other value positions.
/// </summary>
public abstract class SqlExpression : QueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlExpression"/>.
    /// </summary>
    /// <param name="location">The source location of the expression.</param>
    protected internal SqlExpression(Location? location)
        : base(null, location ?? Location.Create(1, 1, 0, 0))
    {
    }

    /// <summary>
    /// Gets the depth of the tree this node roots: the number of nodes on its longest path to
    /// a leaf, this node included, so a leaf is 1 and <c>1 + 1 + 1</c> is 3. A node computes it
    /// from its children when it is constructed, which keeps it constant-time to read for trees
    /// of any depth. Parentheses are not nodes and add nothing; a subquery adds the depth of
    /// its own clauses. A node type defined outside this assembly is a leaf here.
    /// </summary>
    /// <remarks>
    /// The parser bounds it with <see cref="SqlQueryParser.MaximumExpressionDepth"/>, and
    /// <see cref="SqlExpressionRenderer"/> refuses a deeper tree, which would not parse back.
    /// </remarks>
    internal int Depth { get; private protected set; } = 1;

    /// <summary>The depth of a node, or 0 for an absent one.</summary>
    internal static int DepthOf(SqlExpression? expression) => expression?.Depth ?? 0;

    /// <summary>The greatest depth among <paramref name="expressions"/>, or 0 when there are none.</summary>
    internal static int DepthOf(IReadOnlyList<SqlExpression>? expressions)
    {
        int depth = 0;
        if (expressions is not null)
        {
            foreach (var expression in expressions)
            {
                depth = Math.Max(depth, expression.Depth);
            }
        }

        return depth;
    }
}
