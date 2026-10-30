using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlTransactionExpression ParseTransaction()
    {
        var start = Current;
        bool requiresKind = Is("START");
        var command = Is("COMMIT") ? OqlTransactionCommand.Commit :
            Is("ROLLBACK") ? OqlTransactionCommand.Rollback : Is("SAVEPOINT") ? OqlTransactionCommand.Savepoint :
            Is("RELEASE") ? OqlTransactionCommand.ReleaseSavepoint : OqlTransactionCommand.Begin;
        Advance();
        bool hasKind = (command is OqlTransactionCommand.Begin or OqlTransactionCommand.Commit or OqlTransactionCommand.Rollback) && (Take("WORK") || Take("TRAN") || Take("TRANSACTION"));
        if (requiresKind && !hasKind)
        {
            Error("CD00002", ErrorMessages.TransactionKindRequired, Current);
        }

        string? name = null;
        if (command == OqlTransactionCommand.Rollback && Take("TO")) { command = OqlTransactionCommand.RollbackToSavepoint; }
        if (command is OqlTransactionCommand.Savepoint or OqlTransactionCommand.ReleaseSavepoint or OqlTransactionCommand.RollbackToSavepoint)
        {
            if (command != OqlTransactionCommand.Savepoint) { Take("SAVEPOINT"); }
            name = Identifier();
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

        return new OqlTransactionExpression(name is { Length: 0 } ? OqlTransactionCommand.Begin : command, isolation, Span(start, Previous), name);
    }
}
