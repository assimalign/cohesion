using Assimalign.Cohesion.Database.Server;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Options controlling the key-value database server front-end. The common
/// surface — the bound transport listener, the authenticator, and the DoS
/// guardrails — is inherited from the shared server core's
/// <see cref="DatabaseServerOptions"/>; key-value-specific server knobs land
/// here as the model's wire surface grows.
/// </summary>
/// <remarks>
/// The options deliberately carry no engine: servers are per-model and the
/// composition root supplies the single engine directly
/// (<see cref="KeyValueDatabaseServer.Create"/>, or the
/// <c>AddKeyValueServer(engine, configure)</c> builder verb).
/// </remarks>
public sealed class KeyValueDatabaseServerOptions : DatabaseServerOptions
{
}
