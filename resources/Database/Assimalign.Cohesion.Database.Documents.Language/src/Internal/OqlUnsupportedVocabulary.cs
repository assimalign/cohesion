namespace Assimalign.Cohesion.Database.Documents.Language.Internal;

/// <summary>
/// The OQL recognized-unsupported table (#1101): every word the capability scan recognizes
/// but the Documents engine does not run, with the construct its <c>CK00001</c> names.
/// </summary>
/// <remarks>
/// Words outside <see cref="OqlLanguageProfile"/>'s keyword list stay identifiers to the
/// lexer, so a field named <c>fetch</c> or <c>merge</c> still parses; their
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
        // Enabled as a collection operator; retained for reserved non-expression positions.
        new("EXISTS", "EXISTS", OqlWordPosition.Anywhere),
        new("FOR", "FOR", OqlWordPosition.Anywhere),
        new("SOME", "SOME", OqlWordPosition.Anywhere),
        new("ANY", "ANY", OqlWordPosition.Anywhere),
        new("WITHIN", "WITHIN", OqlWordPosition.Clause),
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
        // Deferred SQL++ function families; their names remain usable as fields.
        new("REGEXP_LIKE", "REGEXP_LIKE", OqlWordPosition.Function),
        new("REGEXP_REPLACE", "REGEXP_REPLACE", OqlWordPosition.Function),
        new("REGEXP_POSITION", "REGEXP_POSITION", OqlWordPosition.Function),
        // CREATE and DROP outside supported collection and index DDL.
        new("CREATE", "CREATE", OqlWordPosition.Anywhere),
        new("DROP", "DROP", OqlWordPosition.Anywhere),
        // Statements and clauses of other languages.
        new("ALTER", "ALTER", OqlWordPosition.Clause),
        new("INSERT", "INSERT", OqlWordPosition.Clause),
        // Enabled for DML; retained for contextual coverage and prefix/SELECT refusal.
        new("RETURNING", "RETURNING", OqlWordPosition.Trailing),
        // Enabled by OqlClauses.Update; SET's positional exemption belongs to UPDATE.
        new("UPDATE", "UPDATE", OqlWordPosition.Clause),
        // Enabled by OqlClauses.Delete; retained here for contextual keyword coverage.
        new("DELETE", "DELETE", OqlWordPosition.Clause),
        new("USE", "USE", OqlWordPosition.Clause),
        // Enabled transaction controls retain contextual keyword coverage.
        new("BEGIN", OqlClauses.Begin, OqlWordPosition.Clause),
        new("START", OqlClauses.Begin, OqlWordPosition.Statement),
        new("COMMIT", OqlClauses.Commit, OqlWordPosition.Clause),
        new("ROLLBACK", OqlClauses.Rollback, OqlWordPosition.Clause),
        new("SAVEPOINT", "SAVEPOINT", OqlWordPosition.Statement),
        new("WITH", "WITH", OqlWordPosition.Clause),
        new("LETTING", "LETTING", OqlWordPosition.Clause),
        new("JOIN", "JOIN", OqlWordPosition.Clause),
        new("NEST", "NEST", OqlWordPosition.Clause),
        new("UNION", "UNION", OqlWordPosition.Clause),
        new("INTERSECT", "INTERSECT", OqlWordPosition.Clause),
        new("EXCEPT", "EXCEPT", OqlWordPosition.Clause),
        // OQL uses LIMIT/OFFSET; the SQL FETCH spelling remains deferred.
        new("FETCH", "FETCH", OqlWordPosition.Clause),
        // SQL++ statement words.
        // Enabled by OqlClauses.Upsert; retained for contextual keyword coverage.
        new("UPSERT", "UPSERT", OqlWordPosition.Statement),
        new("MERGE", "MERGE", OqlWordPosition.Statement),
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
