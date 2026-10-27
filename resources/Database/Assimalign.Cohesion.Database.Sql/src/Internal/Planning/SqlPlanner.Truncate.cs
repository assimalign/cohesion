using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private void UseTruncateSnapshot(TransactionSnapshot? snapshot)
    {
        if (ViewImageState is not null || snapshot is null ||
            _catalog.CaptureTruncateReadSnapshot(snapshot) is not { } retained) { return; }
        var directory = _viewImages.GetValue(_catalog, _ => new());
        lock (directory.Sync)
        {
            ViewImageState = FindViewImage(directory, retained,
                () => new SqlBindingEntry<SqlFunctionEnvironment>(new SqlFunctionEnvironment(_functions.Catalog, _functions.Database, _functions.Types)));
        }
    }
}
