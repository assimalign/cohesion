namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

/// <summary>Contextual words of the standalone transaction grammar (#1540, #1725).</summary>
internal static class GqlTransactionVocabulary
{
    internal const string Begin = "BEGIN";
    internal const string Start = "START";
    internal const string Transaction = "TRANSACTION";
    internal const string Commit = "COMMIT";
    internal const string Rollback = "ROLLBACK";

    // These words do not become lexer keywords. The statement parser owns their supported
    // positions; the unsupported table retains existing refusals in composed statements.
    internal static readonly string[] Spellings = [Begin, Start, Transaction, Commit, Rollback];
}
