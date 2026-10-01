namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

/// <summary>
/// The SQL recognized-unsupported table (#1101): every word or operator outside the profile's
/// keyword list that the parser recognizes as an unsupported construct and rejects with
/// <c>COHDBL001</c>, plus the clause keywords the preflight scan matches through this table,
/// each with the construct its diagnostic names.
/// </summary>
/// <remarks>
/// <para>
/// Profile keywords that start an unsupported construct by another rule, such as <c>LEFT</c> or
/// <c>CROSS</c>, have no entry; the keyword-disposition corpus covers them through the profile.
/// Function names, such as the window functions, are not entries either: shared-diagnostics
/// extends the table with them.
/// </para>
/// <para>
/// The preflight clause scan matches the <see cref="SqlWordPosition.Clause"/> entries wherever
/// the lexer reports them as keywords. The other entries are matched by the rule their
/// position names, at the parser branch that recognizes the construct. An item that adds an
/// entry adds its case to the keyword-disposition corpus in the same change, or
/// <c>SqlKeywordDispositionTests</c> fails. The corpus also checks that each entry's diagnostic
/// names the entry's construct.
/// </para>
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
        new("VIEW", "VIEW", SqlWordPosition.Rule),
        new("COLLATION", "user-defined collations (CREATE COLLATION)", SqlWordPosition.Rule),
        new("FULLTEXT", "collation-aware full-text indexes", SqlWordPosition.Rule),
        new("NULLS", "NULLS FIRST and NULLS LAST", SqlWordPosition.Rule),
        new("GROUPING", "GROUPING", SqlWordPosition.Rule),
        new("ROLLUP", "ROLLUP", SqlWordPosition.Rule),
        new("CUBE", "CUBE", SqlWordPosition.Rule),
        new("GROUPING_ID", "GROUPING_ID", SqlWordPosition.Rule),
        new("FILTER", "FILTER", SqlWordPosition.Rule),
        new("WITHIN", "WITHIN GROUP", SqlWordPosition.Rule),
        new("ANY", "ANY quantified comparison", SqlWordPosition.Rule),
        new("SOME", "SOME quantified comparison", SqlWordPosition.Rule),
        new("LATERAL", "LATERAL subquery", SqlWordPosition.Rule),
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
/// <param name="Construct">
/// The construct the diagnostic names. A word that starts more than one construct names the
/// text they share: <c>VIEW</c> for <c>CREATE VIEW</c> and <c>DROP VIEW</c>, and <c>GROUPING</c>
/// for <c>GROUPING SETS</c> and the <c>GROUPING(...)</c> call.
/// </param>
/// <param name="Position">Where the parser treats the spelling as that construct.</param>
internal readonly record struct SqlUnsupportedWord(string Spelling, string Construct, SqlWordPosition Position);

/// <summary>Where the SQL parser treats a recognized-unsupported spelling as its construct.</summary>
internal enum SqlWordPosition
{
    /// <summary>Wherever the lexer reports the keyword, found by the preflight clause scan.</summary>
    Clause,

    /// <summary>
    /// Where a dedicated rule recognizes it: <c>WITH</c> as a statement prefix; <c>ALL</c> after
    /// <c>SELECT</c> or a comparison operator; <c>VIEW</c> after <c>CREATE</c> or <c>DROP</c>;
    /// <c>COLLATION</c> and <c>FULLTEXT</c> after <c>CREATE</c>; <c>NULLS FIRST</c>/<c>LAST</c> in
    /// <c>ORDER BY</c>; <c>GROUPING SETS</c> and the <c>GROUPING</c>, <c>ROLLUP</c>, <c>CUBE</c> and
    /// <c>GROUPING_ID</c> calls; <c>FILTER (...)</c> and <c>WITHIN GROUP</c> after a call;
    /// <c>ANY</c> and <c>SOME</c> between a comparison operator and <c>(</c>; <c>LATERAL</c> after
    /// <c>FROM</c> or <c>JOIN</c>; and <c>~</c> in prefix or infix position.
    /// </summary>
    Rule,
}
