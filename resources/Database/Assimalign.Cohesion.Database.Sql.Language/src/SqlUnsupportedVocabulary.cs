namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// The SQL recognized-unsupported table (#1101): every spelling the parser recognizes but the
/// SQL engine does not run, with the construct its <c>COHDBL001</c> names.
/// </summary>
/// <remarks>
/// The preflight clause scan matches the <see cref="SqlWordPosition.Clause"/> entries wherever
/// the lexer reports them as keywords. The other entries are matched by the rule their
/// position names, at the parser branch that recognizes the construct. An item that adds an
/// entry adds its case to the keyword-disposition corpus in the same change, or
/// <c>SqlKeywordDispositionTests</c> fails.
/// </remarks>
internal static class SqlUnsupportedVocabulary
{
    /// <summary>The table, in the order the dialect documents it.</summary>
    internal static readonly SqlUnsupportedWord[] Words =
    [
        new("UNION", SqlClauses.SetOperation, SqlWordPosition.Clause),
        new("INTERSECT", SqlClauses.Intersect, SqlWordPosition.Clause),
        new("EXCEPT", SqlClauses.Except, SqlWordPosition.Clause),
        new("RECURSIVE", SqlClauses.Recursive, SqlWordPosition.Clause),
        new("WINDOW", SqlClauses.Window, SqlWordPosition.Clause),
        new("FETCH", SqlClauses.Fetch, SqlWordPosition.Clause),
        new("OVER", SqlClauses.Over, SqlWordPosition.Clause),
        new("PARTITION", SqlClauses.Partition, SqlWordPosition.Clause),
        new("RETURNING", SqlClauses.Returning, SqlWordPosition.Clause),
        new("TOP", SqlClauses.Top, SqlWordPosition.Clause),
        new("NATURAL", SqlClauses.Natural, SqlWordPosition.Clause),
        new("USING", SqlClauses.Using, SqlWordPosition.Clause),
        new("WITH", SqlClauses.Cte, SqlWordPosition.Rule),
        new("ALL", SqlClauses.All, SqlWordPosition.Rule),
        new("VIEW", SqlClauses.CreateView, SqlWordPosition.Rule),
        new("COLLATION", "user-defined collations (CREATE COLLATION)", SqlWordPosition.Rule),
        new("FULLTEXT", "collation-aware full-text indexes", SqlWordPosition.Rule),
        new("NULLS", "NULLS FIRST and NULLS LAST", SqlWordPosition.Rule),
        new("~", "~ operator", SqlWordPosition.Rule),
    ];

    /// <summary>Finds the clause a <see cref="SqlWordPosition.Clause"/> keyword names.</summary>
    /// <param name="keyword">The keyword as written.</param>
    /// <param name="clause">The clause name, when the keyword is a clause entry.</param>
    /// <returns><see langword="true"/> when the keyword is a clause entry.</returns>
    internal static bool TryGetClause(string keyword, out string clause)
    {
        foreach (var word in Words)
        {
            if (word.Position == SqlWordPosition.Clause &&
                string.Equals(word.Spelling, keyword, System.StringComparison.OrdinalIgnoreCase))
            {
                clause = word.Construct;
                return true;
            }
        }

        clause = string.Empty;
        return false;
    }
}

/// <summary>One entry of <see cref="SqlUnsupportedVocabulary"/>.</summary>
/// <param name="Spelling">The upper-case word or the operator.</param>
/// <param name="Construct">The construct the diagnostic names.</param>
/// <param name="Position">Where the parser treats the spelling as that construct.</param>
internal readonly record struct SqlUnsupportedWord(string Spelling, string Construct, SqlWordPosition Position);

/// <summary>Where the SQL parser treats a recognized-unsupported spelling as its construct.</summary>
internal enum SqlWordPosition
{
    /// <summary>Wherever the lexer reports the keyword, found by the preflight clause scan.</summary>
    Clause,

    /// <summary>
    /// Where a dedicated rule recognizes it: <c>WITH</c> as a statement prefix, <c>ALL</c>
    /// after <c>SELECT</c>, <c>VIEW</c> after <c>CREATE</c> or <c>DROP</c>, <c>COLLATION</c> and
    /// <c>FULLTEXT</c> after <c>CREATE</c>, <c>NULLS FIRST</c>/<c>LAST</c> in <c>ORDER BY</c>,
    /// and <c>~</c> in prefix or infix position.
    /// </summary>
    Rule,
}
