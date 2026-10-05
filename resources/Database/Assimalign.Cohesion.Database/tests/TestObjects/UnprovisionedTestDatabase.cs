using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A database that claims schema provisioning without overriding its core, so the base's default
/// core answers.
/// </summary>
internal sealed class UnprovisionedTestDatabase : DatabaseInstance
{
    public UnprovisionedTestDatabase(DatabaseName name, DatabaseEngine engine)
        : base(name, engine, supportsSchemaProvisioning: true)
    {
    }

    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => new(new TestSession(this));

    protected override void DisposeCore()
    {
    }

    protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}
