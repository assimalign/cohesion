using System;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// How a relay ended: the bytes it moved each way, the first failure, and whether the activity
/// timeout ended it.
/// </summary>
internal readonly record struct StreamRelayResult(long BytesToDestination, long BytesToClient, Exception? Failure, bool TimedOut);
