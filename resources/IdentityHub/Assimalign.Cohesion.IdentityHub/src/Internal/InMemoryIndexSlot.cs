namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The name and value of an index entry, as the in-memory store keys its index maps. Both
/// compare ordinally.
/// </summary>
/// <param name="Name">The index's name.</param>
/// <param name="Value">The value listed under the index.</param>
internal readonly record struct InMemoryIndexSlot(string Name, string Value);
