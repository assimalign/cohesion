using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlTransactionExpression ParseTransaction()
    {
        var start = Current;
        bool requiresKind = Is("START");
        var command = Is("COMMIT") ? OqlTransactionCommand.Commit :
            Is("ROLLBACK") ? OqlTransactionCommand.Rollback : OqlTransactionCommand.Begin;
        Advance();
        bool hasKind = Take("WORK") || Take("TRAN") || Take("TRANSACTION");
        if (requiresKind && !hasKind)
        {
            Error("CD00002", ErrorMessages.TransactionKindRequired, Current);
        }

        if (command == OqlTransactionCommand.Rollback && Is("TO"))
        {
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("TO", Profile.Language, Span(Current, Current)));
        }

        var isolation = OqlTransactionIsolation.Snapshot;
        if (command == OqlTransactionCommand.Begin && Take("ISOLATION"))
        {
            Expect("LEVEL");
            if (Take("READ"))
            {
                Expect("COMMITTED");
                isolation = OqlTransactionIsolation.ReadCommitted;
            }
            else if (!Take("SNAPSHOT"))
            {
                Error("CD00002", ErrorMessages.TransactionIsolationRequired, Current);
            }
        }

        return new OqlTransactionExpression(command, isolation, Span(start, Previous));
    }
}
