using System;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// A stored document: identity, version, and UTF-8 JSON content.
/// </summary>
/// <param name="Id">The document identity within its collection.</param>
/// <param name="Version">The stored version, for optimistic concurrency.</param>
/// <param name="Content">The document content as UTF-8 JSON.</param>
public readonly record struct Document(DocumentId Id, DocumentVersion Version, ReadOnlyMemory<byte> Content);
