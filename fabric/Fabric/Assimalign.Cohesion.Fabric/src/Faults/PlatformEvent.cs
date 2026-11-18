using System;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One platform event behind a <see cref="PlatformDetail"/>.
/// </summary>
/// <param name="Reason">The event's reason, such as <c>Failed</c>.</param>
/// <param name="Count">How many times the platform reported it.</param>
/// <param name="Message">The event's message.</param>
/// <param name="LastSeen">When the platform last reported it, if known.</param>
public sealed record PlatformEvent(string Reason, int Count, string? Message, DateTimeOffset? LastSeen = null);
