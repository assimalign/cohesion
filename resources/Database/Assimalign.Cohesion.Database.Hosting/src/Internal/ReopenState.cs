using System;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// A database the application is reopening (<see cref="DatabaseReopenService"/>), as its health
/// reports it.
/// </summary>
/// <param name="Engine">The engine.</param>
/// <param name="Name">The database's name.</param>
/// <param name="Cause">What took it offline, or null when the engine does not say.</param>
/// <param name="Attempts">The reopen attempts so far.</param>
/// <param name="LastFailure">The last attempt's failure, or null before a failed attempt.</param>
/// <param name="NextDelay">The delay chosen before the next attempt.</param>
internal sealed record ReopenState(DatabaseEngine Engine, string Name, StorageOfflineCause? Cause, int Attempts, Exception? LastFailure, TimeSpan NextDelay);
