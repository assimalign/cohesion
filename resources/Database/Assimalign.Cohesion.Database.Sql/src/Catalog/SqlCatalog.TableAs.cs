using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    // Engine-local entry point: reserve a new identity and its kind-14 abandonment
    // intent atomically. The caller's statement epoch protects the build from reclamation.
    internal ValueTask<SqlCatalogTable> ReserveTableAsAsync(string schema, string name,
        IReadOnlyList<SqlCatalogColumn> columns, string? owningSchema, SqlCatalogScriptPublication? publication, CancellationToken cancellationToken)
        => ReserveTableCoreAsync(schema, name, columns, [], [],
            owningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema, owningSchema,
            cancellationToken, publication, prepareOwner: true);
}
