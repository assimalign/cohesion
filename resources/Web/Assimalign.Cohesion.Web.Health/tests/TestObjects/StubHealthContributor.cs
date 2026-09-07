using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Web.Health.Tests;

/// <summary>
/// A configurable transport-neutral health contributor used to exercise the Web health adapter.
/// </summary>
internal sealed class StubHealthContributor : IHealthContributor
{
    private readonly Func<CancellationToken, ValueTask<HealthContribution>> _check;

    public StubHealthContributor(string name, HealthContribution contribution)
        : this(name, _ => ValueTask.FromResult(contribution))
    {
    }

    public StubHealthContributor(
        string name,
        Func<CancellationToken, ValueTask<HealthContribution>> check)
    {
        Name = name;
        _check = check;
    }

    public string Name { get; }

    public int Invocations { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        Invocations++;
        LastCancellationToken = cancellationToken;

        return _check(cancellationToken);
    }
}
