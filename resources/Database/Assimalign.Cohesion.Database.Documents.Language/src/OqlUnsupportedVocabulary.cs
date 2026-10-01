namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// The OQL recognized-unsupported table (#1101): every word the capability scan recognizes
/// but the Documents engine does not run, with the construct its <c>COHDBL001</c> names.
/// </summary>
/// <remarks>
/// Words outside <see cref="OqlLanguageProfile"/>'s keyword list stay identifiers to the
/// lexer, so a field named <c>limit</c> or <c>merge</c> still parses; their
/// <see cref="OqlWordPosition"/> says where the scan treats them as a construct instead. An
/// item that adds an entry adds its case to the keyword-disposition corpus in the same
/// change, or <c>OqlKeywordDispositionTests</c> fails.
/// </remarks>
internal static class OqlUnsupportedVocabulary
{
    /// <summary>The table, in the order the scan documents it.</summary>
    internal static readonly OqlUnsupportedWord[] Words =
    [
        // ODMG constructs the planner does not run. oql-sqlpp-basis owns their removal.
        new("DEFINE", OqlClauses.Define, OqlWordPosition.Anywhere),
        new("ELEMENT", OqlClauses.Element, OqlWordPosition.Anywhere),
        new("FLATTEN", OqlClauses.Flatten, OqlWordPosition.Anywhere),
        new("DISTINCT", "DISTINCT", OqlWordPosition.Anywhere),
        new("ALL", "ALL", OqlWordPosition.Anywhere),
        new("IN", "IN", OqlWordPosition.Anywhere),
        new("EXISTS", "EXISTS", OqlWordPosition.Anywhere),
        new("LIKE", "LIKE", OqlWordPosition.Anywhere),
        new("BETWEEN", "BETWEEN", OqlWordPosition.Anywhere),
        new("FOR", "FOR", OqlWordPosition.Anywhere),
        new("SOME", "SOME", OqlWordPosition.Anywhere),
        new("ANY", "ANY", OqlWordPosition.Anywhere),
        new("STRUCT", "STRUCT", OqlWordPosition.Anywhere),
        new("LIST", "LIST", OqlWordPosition.Anywhere),
        new("SET", "SET", OqlWordPosition.Anywhere),
        new("BAG", "BAG", OqlWordPosition.Anywhere),
        new("ARRAY", "ARRAY", OqlWordPosition.Anywhere),
        new("COLLECTION", "COLLECTION", OqlWordPosition.Anywhere),
        new("FIRST", "FIRST", OqlWordPosition.Anywhere),
        new("LAST", "LAST", OqlWordPosition.Anywhere),
        new("UNIQUE", "UNIQUE", OqlWordPosition.Anywhere),
        new("LISTTOSET", "LISTTOSET", OqlWordPosition.Anywhere),
        new("TYPEOF", "TYPEOF", OqlWordPosition.Anywhere),
        new("UNDEFINED", "UNDEFINED", OqlWordPosition.Anywhere),
        new("ABS", "ABS", OqlWordPosition.Anywhere),
        // CREATE and DROP other than CREATE INDEX and DROP INDEX.
        new("CREATE", "CREATE", OqlWordPosition.Anywhere),
        new("DROP", "DROP", OqlWordPosition.Anywhere),
        // Statements and clauses of other languages.
        new("ALTER", "ALTER", OqlWordPosition.Clause),
        new("INSERT", "INSERT", OqlWordPosition.Clause),
        new("UPDATE", "UPDATE", OqlWordPosition.Clause),
        new("DELETE", "DELETE", OqlWordPosition.Clause),
        new("USE", "USE", OqlWordPosition.Clause),
        new("BEGIN", "BEGIN", OqlWordPosition.Clause),
        new("COMMIT", "COMMIT", OqlWordPosition.Clause),
        new("ROLLBACK", "ROLLBACK", OqlWordPosition.Clause),
        new("WITH", "WITH", OqlWordPosition.Clause),
        new("JOIN", "JOIN", OqlWordPosition.Clause),
        new("UNION", "UNION", OqlWordPosition.Clause),
        new("INTERSECT", "INTERSECT", OqlWordPosition.Clause),
        new("EXCEPT", "EXCEPT", OqlWordPosition.Clause),
        // SQL++ words. oql-limit-offset, oql-upsert and oql-arrays flip these pins; MERGE stays.
        new("LIMIT", "LIMIT", OqlWordPosition.Clause),
        new("OFFSET", "OFFSET", OqlWordPosition.Clause),
        new("UPSERT", "UPSERT", OqlWordPosition.Clause),
        new("MERGE", "MERGE", OqlWordPosition.Clause),
        new("UNNEST", "UNNEST", OqlWordPosition.Clause),
        new("EVERY", "EVERY ... SATISFIES", OqlWordPosition.Quantifier),
        new("SATISFIES", "SATISFIES", OqlWordPosition.Clause),
    ];

    /// <summary>Finds the entry for an unquoted word, compared case-insensitively.</summary>
    /// <param name="upperWord">The word, already upper-cased with the invariant culture.</param>
    /// <param name="word">The entry, when the word is in the table.</param>
    /// <returns><see langword="true"/> when the word is in the table.</returns>
    internal static bool TryFind(string upperWord, out OqlUnsupportedWord word)
    {
        foreach (var candidate in Words)
        {
            if (candidate.Spelling == upperWord)
            {
                word = candidate;
                return true;
            }
        }

        word = default;
        return false;
    }
}

/// <summary>One entry of <see cref="OqlUnsupportedVocabulary"/>.</summary>
/// <param name="Spelling">The upper-case word.</param>
/// <param name="Construct">The construct the diagnostic names.</param>
/// <param name="Position">Where the capability scan treats the word as that construct.</param>
internal readonly record struct OqlUnsupportedWord(string Spelling, string Construct, OqlWordPosition Position);

/// <summary>Where the OQL capability scan treats a recognized-unsupported word as its construct.</summary>
internal enum OqlWordPosition
{
    /// <summary>Everywhere except a path segment after a dot. All are lexer keywords or functions.</summary>
    Anywhere,

    /// <summary>
    /// As the first word of the statement, or directly after the end of an operand (a name,
    /// literal, parameter, closing bracket, or <c>ASC</c>/<c>DESC</c>). Elsewhere it is a name.
    /// </summary>
    Clause,

    /// <summary>
    /// Opening a quantified predicate, <c>EVERY x IN ... SATISFIES ... END</c>, and otherwise
    /// as <see cref="Clause"/>. <c>ANY</c> and <c>SOME</c> open the same predicate.
    /// </summary>
    Quantifier,
}
