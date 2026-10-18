namespace Assimalign.Cohesion.Database.Documents.Language.Internal;

/// <summary>
/// The OQL recognized-unsupported table (#1101): every word the capability scan recognizes
/// but the Documents engine does not run, with the construct its <c>COHDBL001</c> names.
/// </summary>
/// <remarks>
/// Words outside <see cref="OqlLanguageProfile"/>'s keyword list stay identifiers to the
/// lexer, so a field named <c>limit</c> or <c>merge</c> still parses; their
/// <see cref="OqlWordPosition"/> says where the scan treats them as a construct instead. An
/// item that adds an entry adds its case to the keyword-disposition corpus in the same
/// change, or <c>OqlKeywordDispositionTests</c> fails. The corpus also checks that each
/// entry's diagnostic names the entry's construct.
/// </remarks>
internal static class OqlUnsupportedVocabulary
{
    /// <summary>The table, in the order the scan documents it.</summary>
    internal static readonly OqlUnsupportedWord[] Words =
    [
        // Retired ODMG constructs stay recognizable only in their construct positions.
        new("DEFINE", "DEFINE", OqlWordPosition.Statement),
        new("ELEMENT", "ELEMENT", OqlWordPosition.Function),
        new("FLATTEN", "FLATTEN", OqlWordPosition.Function),
        new("EXISTS", "EXISTS", OqlWordPosition.Anywhere),
        new("FOR", "FOR", OqlWordPosition.Anywhere),
        new("SOME", "SOME", OqlWordPosition.Anywhere),
        new("ANY", "ANY", OqlWordPosition.Anywhere),
        new("STRUCT", "STRUCT", OqlWordPosition.Function),
        new("LIST", "LIST", OqlWordPosition.Function),
        new("SET", "SET", OqlWordPosition.Anywhere),
        new("BAG", "BAG", OqlWordPosition.Function),
        new("ARRAY", "ARRAY", OqlWordPosition.Anywhere),
        new("COLLECTION", "COLLECTION", OqlWordPosition.Anywhere),
        new("FIRST", "FIRST", OqlWordPosition.Anywhere),
        new("LAST", "LAST", OqlWordPosition.Anywhere),
        new("UNIQUE", "UNIQUE", OqlWordPosition.Anywhere),
        new("LISTTOSET", "LISTTOSET", OqlWordPosition.Function),
        new("TYPEOF", "TYPEOF", OqlWordPosition.Function),
        new("UNDEFINED", "UNDEFINED", OqlWordPosition.Anywhere),
        new("KNOWN", "KNOWN", OqlWordPosition.Anywhere),
        new("UNKNOWN", "UNKNOWN", OqlWordPosition.Anywhere),
        new("ABS", "ABS", OqlWordPosition.Function),
        // CREATE and DROP outside supported collection and index DDL.
        new("CREATE", "CREATE", OqlWordPosition.Anywhere),
        new("DROP", "DROP", OqlWordPosition.Anywhere),
        // Statements and clauses of other languages.
        new("ALTER", "ALTER", OqlWordPosition.Clause),
        new("INSERT", "INSERT", OqlWordPosition.Clause),
        new("RETURNING", "RETURNING", OqlWordPosition.Trailing),
        new("UPDATE", "UPDATE", OqlWordPosition.Clause),
        // Enabled by OqlClauses.Delete; retained here for contextual keyword coverage.
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
        new("UPSERT", "UPSERT", OqlWordPosition.Statement),
        new("MERGE", "MERGE", OqlWordPosition.Statement),
        new("UNNEST", "UNNEST", OqlWordPosition.Clause),
        new("EVERY", "EVERY ... SATISFIES", OqlWordPosition.Quantifier),
        new("SATISFIES", "SATISFIES", OqlWordPosition.Quantifier),
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
    /// So a <see cref="Clause"/> word cannot be an AS-less <c>FROM</c> alias; <c>AS</c> keeps it
    /// a name.
    /// </summary>
    Clause,

    /// <summary>
    /// After a parsed statement, so destination, binding and other identifier positions
    /// are consumed as names first. The capability scan still rejects a statement prefix.
    /// </summary>
    Trailing,

    /// <summary>
    /// Only as the first word of the statement, where it is a statement verb of another
    /// language (<c>UPSERT INTO c ...</c>). Elsewhere, including an AS-less <c>FROM</c> alias, it
    /// is a name.
    /// </summary>
    Statement,

    /// <summary>Only before a function call's opening parenthesis; otherwise an identifier.</summary>
    Function,

    /// <summary>
    /// Only as part of a quantified predicate, <c>EVERY x IN ... SATISFIES ... END</c>, which the
    /// scan recognizes by its <c>name IN</c> binding and reports once at the quantifier.
    /// <c>ANY</c> and <c>SOME</c> open the same predicate. Elsewhere it is a name.
    /// </summary>
    Quantifier,
}
