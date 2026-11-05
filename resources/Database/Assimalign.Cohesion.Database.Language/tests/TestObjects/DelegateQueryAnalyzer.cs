using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Language.Tests;

internal sealed class DelegateQueryAnalyzer : QueryAnalyzer
{
    private readonly Func<QueryAnalyzerContext, CancellationToken, Task> _analyze;

    internal DelegateQueryAnalyzer(Func<QueryAnalyzerContext, CancellationToken, Task> analyze)
    {
        _analyze = analyze;
    }

    protected override Task AnalyzeCoreAsync(QueryAnalyzerContext context, CancellationToken cancellationToken)
        => _analyze(context, cancellationToken);
}
