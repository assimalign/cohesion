namespace Assimalign.Cohesion.Database.Server.Tests;

/// <summary>
/// A minimal per-model server over the guided base — what every model server
/// (<c>SqlDatabaseServer</c>, …) looks like structurally. Deriving it here also
/// proves the base is derivable from outside its assembly.
/// </summary>
internal sealed class TestDatabaseServer : DatabaseServer
{
    internal TestDatabaseServer(IDatabaseEngine engine, DatabaseServerOptions options)
        : base(engine, options)
    {
    }
}
