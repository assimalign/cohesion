using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

/// <summary>
/// A custom <see cref="IAuthorizationRequirement"/> with a fixed outcome that counts its evaluations, to
/// prove custom requirements take part in a policy and that evaluation stops at the first failure.
/// </summary>
internal sealed class RecordingRequirement : IAuthorizationRequirement
{
    private readonly bool _result;
    private int _evaluations;

    public RecordingRequirement(bool result)
    {
        _result = result;
    }

    public int Evaluations => Volatile.Read(ref _evaluations);

    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _evaluations);
        return new ValueTask<bool>(_result);
    }
}
