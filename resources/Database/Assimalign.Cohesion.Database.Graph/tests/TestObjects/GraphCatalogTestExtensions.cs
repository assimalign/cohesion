using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Tests;

// Test-only exchange over the shared DatabaseProtocolExchange base; Graph.Client's own
// GraphExecuteExchange is internal, so the catalog tests keep this double.
internal static partial class GraphCatalogTestExtensions
{
    extension(DatabaseConnection connection)
    {
        internal ValueTask<GraphCatalogTestResult> ExecuteAsync(string statement, CancellationToken cancellationToken = default)
            => connection.ExecuteAsync(new GraphCatalogTestExchange(statement), cancellationToken);
    }
}
