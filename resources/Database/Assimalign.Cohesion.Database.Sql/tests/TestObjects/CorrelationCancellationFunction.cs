using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal sealed class CorrelationCancellationFunction : SqlScalarFunction
{
    private readonly CancellationTokenSource _cancellation;
    private int _calls;

    internal CorrelationCancellationFunction(CancellationTokenSource cancellation)
        : base("cancel_third", [SqlType.Integer], SqlType.Boolean)
    {
        _cancellation = cancellation;
    }

    internal int Calls => _calls;
    internal CancellationToken LastToken { get; private set; }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        LastToken = arguments.Context.CancellationToken;
        if (Interlocked.Increment(ref _calls) == 3) { _cancellation.Cancel(); }
        LastToken.ThrowIfCancellationRequested();
        return SqlValue.FromBoolean(true);
    }
}
