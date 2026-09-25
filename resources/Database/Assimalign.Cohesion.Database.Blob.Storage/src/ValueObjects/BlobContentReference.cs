namespace Assimalign.Cohesion.Database.Blob.Storage;

/// <summary>Identifies one immutable chunk chain and its content integrity metadata.</summary>
/// <param name="Head">The packed location of the first chunk, or zero for empty content.</param>
/// <param name="Length">The total content length in bytes.</param>
/// <param name="Checksum">The IEEE CRC-32 of the complete content.</param>
public readonly record struct BlobContentReference(ulong Head, long Length, uint Checksum);
