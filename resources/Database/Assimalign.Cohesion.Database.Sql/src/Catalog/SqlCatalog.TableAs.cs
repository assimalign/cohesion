using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    // Engine-local entry point: reserve a new identity and its kind-14 abandonment
    // intent atomically. The caller's statement epoch protects the build from reclamation.
    internal ValueTask<SqlCatalogTable?> ReserveTableAsAsync(string schema, string name,
        IReadOnlyList<SqlCatalogColumn> columns, string? owningSchema, SqlCatalogScriptPublication? publication,
        bool ifNotExists, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            if (ifNotExists && _tables.ContainsKey((schema, name))) { return ValueTask.FromResult<SqlCatalogTable?>(null); }
            // Catalog mutations complete synchronously under the monitor, including their
            // storage commit. Keep the conditional check and reservation in that same lock.
            return ValueTask.FromResult<SqlCatalogTable?>(ReserveTableCoreAsync(schema, name, columns, [], [],
                owningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema, owningSchema,
                cancellationToken, publication, prepareOwner: true).GetAwaiter().GetResult());
        }
    }

    internal ValueTask<bool> PublishTableAsAsync(SqlCatalogTable table, bool ifNotExists,
        SqlCatalogScriptPublication? publication, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            if (ifNotExists && _tables.ContainsKey((table.Schema, table.Name))) { return ValueTask.FromResult(false); }
            PublishTableCoreAsync(table, [], [], false, cancellationToken, publication).GetAwaiter().GetResult();
            return ValueTask.FromResult(true);
        }
    }
}
