using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

internal static class DocumentPlanTestHelper
{
    // Plan any statement, including DML, inside its own session operation.
    internal static async ValueTask<DocumentStatementPlan> PlanAsync(DocumentDatabase database, string statement,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var session = await database.CreateSessionAsync(cancellationToken);
        return await database.RunAsync(session, operation => ValueTask.FromResult(new DocumentPlanner(database.Catalog,
            operation.Context.Snapshot, parameters, cancellationToken).Plan(
                DocumentQueryRequest.FromOql(statement, parameters).Statement.OqlExpression)), cancellationToken);
    }
}
