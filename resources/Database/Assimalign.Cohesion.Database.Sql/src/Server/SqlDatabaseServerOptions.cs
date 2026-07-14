using Assimalign.Cohesion.Database.Server;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Options controlling the SQL database server front-end. The common surface —
/// the bound transport listener, the authenticator, and the DoS guardrails —
/// is inherited from the shared server core's <see cref="DatabaseServerOptions"/>;
/// SQL-specific server knobs land here as the protocol's model-specific surface
/// grows.
/// </summary>
/// <remarks>
/// The options deliberately carry no engine: servers are per-model and the
/// composition root supplies the single engine directly
/// (<see cref="SqlDatabaseServer.Create"/>, or the <c>AddSqlServer(engine, configure)</c>
/// builder verb).
/// </remarks>
public sealed class SqlDatabaseServerOptions : DatabaseServerOptions
{
}
