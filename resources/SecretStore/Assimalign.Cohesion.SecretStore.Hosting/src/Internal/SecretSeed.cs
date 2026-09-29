using System;

namespace Assimalign.Cohesion.SecretStore.Hosting.Internal;

/// <summary>
/// A secret declared through <c>AddSecret</c>, registered as a singleton so the repository seeds
/// from the resolved declarations.
/// </summary>
/// <param name="Path">The logical store path.</param>
/// <param name="Value">A snapshot of the declared secret bytes.</param>
internal sealed record SecretSeed(string Path, ReadOnlyMemory<byte> Value);
