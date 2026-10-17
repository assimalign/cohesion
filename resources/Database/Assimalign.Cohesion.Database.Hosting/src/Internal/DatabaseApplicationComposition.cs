using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// What one application Build composed: the snapshotted host settings, the context with its frozen
/// registries, what the application owns, and the lifecycle services in start order (the servers'
/// host services follow them).
/// </summary>
/// <param name="Options">The host settings, copied at Build.</param>
/// <param name="Context">The application context.</param>
/// <param name="Ownership">The products the application disposes.</param>
/// <param name="Services">The lifecycle services, started before the servers.</param>
internal sealed record DatabaseApplicationComposition(
    DatabaseApplicationOptions Options,
    DatabaseApplicationContext Context,
    DatabaseApplicationOwnership Ownership,
    IReadOnlyList<IHostService> Services);
