namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

/// <summary>
/// The SQL recognized-unsupported table (#1101): every word or operator outside the profile's
/// keyword list that the parser recognizes as an unsupported construct and rejects with
/// <c>CK00001</c>, plus the clause keywords the preflight scan matches through this table,
/// each with the construct its diagnostic names.
/// </summary>
/// <remarks>
/// <para>
/// Profile keywords that start an unsupported construct by another rule, such as <c>LEFT</c> or
/// <c>CROSS</c>, have no entry; the keyword-disposition corpus covers them through the profile.
/// Non-executing window function names have call-position entries (#1103).
/// Niladic datetime names execute and are recognized separately (#1120).
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
        new("FOR", "FOR UPDATE/SHARE", SqlWordPosition.Rule),
        new("WINDOW", SqlClauses.Window, SqlWordPosition.Clause),
        new("PERCENT", "PERCENT", SqlWordPosition.Rule),
        new("TIES", "WITH TIES", SqlWordPosition.Rule),
        new("OVER", SqlClauses.Over, SqlWordPosition.Clause),
        new("PARTITION", SqlClauses.Partition, SqlWordPosition.Clause),
        new("NATURAL", SqlClauses.Natural, SqlWordPosition.Clause),
        new("USING", SqlClauses.Using, SqlWordPosition.Clause),
        new("ALL", SqlClauses.All, SqlWordPosition.Rule),
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
        new("GROUPING", "GROUPING", SqlWordPosition.Rule),
        new("ROLLUP", "ROLLUP", SqlWordPosition.Rule),
        new("CUBE", "CUBE", SqlWordPosition.Rule),
        new("GROUPING_ID", "GROUPING_ID", SqlWordPosition.Rule),
        new("FILTER", "FILTER", SqlWordPosition.Rule),
        new("WITHIN", "WITHIN GROUP", SqlWordPosition.Rule),
        new("ANY", "ANY array comparison", SqlWordPosition.Rule),
        new("SOME", "SOME array comparison", SqlWordPosition.Rule),
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
