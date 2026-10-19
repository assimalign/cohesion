using System;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Options for <see cref="SqlQueryParser"/>: the analyzers every query parser runs, and the SQL
/// dialect's expression nesting limit (#1151).
/// </summary>
/// <remarks>
/// The limit bounds two things, each at <see cref="ExpressionNestingLimit"/> levels: the depth of
/// the expression tree, in which a chain of <c>AND</c> (or <c>OR</c>) terms is one level however
/// many terms it has, and the nesting of grouping parentheses. Text past either is
/// <c>SQL0006</c>. A SQL engine passes its own configured limit to the parser it executes
/// statement text with; the documented rule is in the dialect's "Expression nesting limit".
/// </remarks>
public sealed class SqlQueryParserOptions : QueryParserOptions
{
    /// <summary>The expression nesting limit a parser applies when none is configured: 256 levels.</summary>
    public const int DefaultExpressionNestingLimit = 256;

    /// <summary>The smallest expression nesting limit a parser or engine accepts: 32 levels.</summary>
    public const int MinimumExpressionNestingLimit = 32;

    /// <summary>
    /// The largest expression nesting limit a parser or engine accepts: 4096 levels. It is also the
    /// ceiling at which the engine reads back definitions it persisted, so a definition stored by an
    /// engine with any configured limit opens on every other.
    /// </summary>
    public const int MaximumExpressionNestingLimit = 4096;

    /// <summary>
    /// Gets or sets how many levels an expression may nest: the deepest expression tree the parser
    /// builds, and the deepest grouping parentheses may nest. Defaults to
    /// <see cref="DefaultExpressionNestingLimit"/>; it must lie within
    /// <see cref="MinimumExpressionNestingLimit"/> and <see cref="MaximumExpressionNestingLimit"/>,
    /// which the parser checks when it is constructed.
    /// </summary>
    public int ExpressionNestingLimit { get; set; } = DefaultExpressionNestingLimit;

    /// <summary>
    /// Returns <paramref name="limit"/> when it lies within the accepted range.
    /// </summary>
    /// <param name="limit">The configured limit.</param>
    /// <param name="paramName">The name of the argument or property that carried it.</param>
    /// <returns><paramref name="limit"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The limit is outside 32..4096.</exception>
    internal static int Validate(int limit, string paramName)
    {
        if (limit is < MinimumExpressionNestingLimit or > MaximumExpressionNestingLimit)
        {
            throw new ArgumentOutOfRangeException(paramName, limit,
                $"The expression nesting limit must be between {MinimumExpressionNestingLimit} and {MaximumExpressionNestingLimit} levels.");
        }

        return limit;
    }
}
