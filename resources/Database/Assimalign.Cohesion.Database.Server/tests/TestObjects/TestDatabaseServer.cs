namespace Assimalign.Cohesion.Database.Server.Tests;

/// <summary>
/// The minimal model-server derivation: proves the guided base is complete —
/// a model package adds only its engine type and options subtype.
/// </summary>
internal sealed class TestDatabaseServer : DatabaseServer
{
    internal TestDatabaseServer(IDatabaseEngine engine, DatabaseServerOptions options)
        : base(engine, options)
    {
    }
}
