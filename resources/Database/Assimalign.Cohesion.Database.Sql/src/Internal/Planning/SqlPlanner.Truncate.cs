using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private readonly TransactionSnapshot? _truncateVisibility;

    private void UseTruncateSnapshot()
    {
        if (ViewImageState is not null || _truncateVisibility is not { } snapshot ||
            _catalog.CaptureTruncateReadSnapshot(snapshot) is not { } retained) { return; }
        var directory = _viewImages.GetValue(_catalog, _ => new());
        lock (directory.Sync)
        {
            ViewImageState = FindViewImage(directory, retained,
                () => new SqlBindingEntry<SqlFunctionEnvironment>(new SqlFunctionEnvironment(_functions.Catalog, _functions.Database, _functions.Types)));
        }
    }
}
