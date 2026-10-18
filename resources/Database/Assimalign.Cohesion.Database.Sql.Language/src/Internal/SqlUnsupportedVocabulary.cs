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
/// Non-executing function names, including window and niladic datetime names, have entries
/// whose positions distinguish calls and ISO niladic forms (#1103).
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
        new("RECURSIVE", SqlClauses.Recursive, SqlWordPosition.Clause),
        new("MATERIALIZED", "[NOT] MATERIALIZED", SqlWordPosition.Rule),
        new("SEARCH", "SEARCH/CYCLE", SqlWordPosition.Rule),
        new("WINDOW", SqlClauses.Window, SqlWordPosition.Clause),
        new("PERCENT", "PERCENT", SqlWordPosition.Rule),
        new("TIES", "WITH TIES", SqlWordPosition.Rule),
        new("OVER", SqlClauses.Over, SqlWordPosition.Clause),
        new("PARTITION", SqlClauses.Partition, SqlWordPosition.Clause),
        new("RETURNING", SqlClauses.Returning, SqlWordPosition.Clause),
        new("NATURAL", SqlClauses.Natural, SqlWordPosition.Clause),
        new("USING", SqlClauses.Using, SqlWordPosition.Clause),
        new("ALL", SqlClauses.All, SqlWordPosition.Rule),
        new("VIEW", "VIEW", SqlWordPosition.Rule),
        new("SEQUENCE", "CREATE SEQUENCE", SqlWordPosition.Rule),
        new("SERIAL", "SERIAL", SqlWordPosition.Rule),
        new("BIGSERIAL", "BIGSERIAL", SqlWordPosition.Rule),
        new("SMALLSERIAL", "SMALLSERIAL", SqlWordPosition.Rule),
        new("OVERRIDING", "OVERRIDING ... VALUE", SqlWordPosition.Rule),
        new("MINVALUE", "IDENTITY MINVALUE/MAXVALUE/CYCLE/CACHE", SqlWordPosition.Rule),
        new("MAXVALUE", "IDENTITY MINVALUE/MAXVALUE/CYCLE/CACHE", SqlWordPosition.Rule),
        new("CYCLE", "IDENTITY MINVALUE/MAXVALUE/CYCLE/CACHE", SqlWordPosition.Rule),
        new("CACHE", "IDENTITY MINVALUE/MAXVALUE/CYCLE/CACHE", SqlWordPosition.Rule),
        new("RESTART", "ALTER COLUMN RESTART", SqlWordPosition.Rule),
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
        // Window functions awaiting execution.
        new("ROW_NUMBER", "ROW_NUMBER", SqlWordPosition.WindowFunction),
        new("RANK", "RANK", SqlWordPosition.WindowFunction),
        new("DENSE_RANK", "DENSE_RANK", SqlWordPosition.WindowFunction),
        new("LEAD", "LEAD", SqlWordPosition.WindowFunction),
        new("LAG", "LAG", SqlWordPosition.WindowFunction),
        new("FIRST_VALUE", "FIRST_VALUE", SqlWordPosition.WindowFunction),
        new("LAST_VALUE", "LAST_VALUE", SqlWordPosition.WindowFunction),
        new("NTH_VALUE", "NTH_VALUE", SqlWordPosition.WindowFunction),
        new("NTILE", "NTILE", SqlWordPosition.WindowFunction),
        new("~", "~ operator", SqlWordPosition.Rule),
    ];

    /// <summary>Whether a name is a recognized function that does not execute.</summary>
    internal static bool IsFunction(string name) => System.Array.Exists(Words, word =>
        word.Position is SqlWordPosition.Function or SqlWordPosition.NiladicFunction or SqlWordPosition.WindowFunction &&
        string.Equals(word.Spelling, name, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a name is a window function awaiting execution.</summary>
    internal static bool IsWindowFunction(string name) => System.Array.Exists(Words, word =>
        word.Position == SqlWordPosition.WindowFunction &&
        string.Equals(word.Spelling, name, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a name has the ISO niladic datetime spelling.</summary>
    internal static bool IsNiladicFunction(string name) => name.ToUpperInvariant() is
        "CURRENT_DATE" or "CURRENT_TIME" or "CURRENT_TIMESTAMP" or "LOCALTIME" or "LOCALTIMESTAMP";

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
    /// <c>GROUPING_ID</c> calls; <c>PERCENT</c> and <c>WITH TIES</c> after FETCH or TOP;
    /// <c>FILTER (...)</c> and <c>WITHIN GROUP</c> after a call;
    /// <c>ANY</c> and <c>SOME</c> between a comparison operator and <c>(</c>; <c>LATERAL</c> after
    /// <c>FROM</c> or <c>JOIN</c>; and <c>~</c> in prefix or infix position.
    /// </summary>
    Rule,

    /// <summary>Before a function's opening parenthesis.</summary>
    Function,

    /// <summary>A window function, owned by the clause preflight.</summary>
    WindowFunction,

    /// <summary>A datetime value function, with or without parentheses.</summary>
    NiladicFunction,
}
