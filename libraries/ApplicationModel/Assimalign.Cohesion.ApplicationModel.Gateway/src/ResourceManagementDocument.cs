using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Contains one durable gateway-owned document with its conditional-write version.</summary>
/// <param name="Version">The storage provider's opaque compare-and-swap token.</param>
/// <param name="Content">The protected document content returned to the trusted gateway.</param>
public sealed record ResourceManagementDocument(string Version, ReadOnlyMemory<byte> Content);
