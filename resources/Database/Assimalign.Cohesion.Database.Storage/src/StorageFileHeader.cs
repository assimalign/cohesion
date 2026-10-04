using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Defines the binary layout of the identity block stored in the body of page 0: the
/// fields that identify a storage file and never change after it is created.
/// </summary>
/// <remarks>
/// <para>
/// Every storage file, regardless of database model, begins with a file header page
/// (<see cref="PageType.FileHeader"/>). Storage format 2 lays page 0 out as follows:
/// </para>
/// <code>
/// Page 0 (8 KiB)
/// ┌──────────────┬──────────────────────┬──────────┬────────────┬──────────┬────────────┐
/// │ page header  │ StorageFileHeader    │ reserved │ header     │ reserved │ header     │
/// │ 0..96        │ 96..352 (identity)   │ ..512    │ slot 0     │ ..4608   │ slot 1     │
/// │              │                      │          │ 512..4096  │          │ 4608..8192 │
/// └──────────────┴──────────────────────┴──────────┴────────────┴──────────┴────────────┘
/// </code>
/// <para>
/// The identity block is written once, when the file is created. Everything that changes —
/// page counts, the LSN and transaction-sequence floors, the checkpoint anchor — lives in two
/// alternating header slots, each with its own generation counter and CRC-32C. A header
/// write goes to the slot that does not hold the newest generation and is made durable
/// before the next one starts, so a write torn by a crash leaves the other slot intact, and
/// open picks the newest slot whose checksum verifies. RavenDB's Voron alternates its
/// <c>headers.one</c> and <c>headers.two</c> the same way
/// (<c>src/Voron/Impl/FileHeaders/HeaderAccessor.cs</c>, <c>Initialize</c> and
/// <c>Modify</c>); PostgreSQL instead keeps <c>pg_control</c> within one 512-byte sector so
/// that its single copy is written atomically (<c>PG_CONTROL_MAX_SAFE_SIZE</c>,
/// <c>src/include/catalog/pg_control.h</c>).
/// </para>
/// <para>
/// Page 0 is never loaded through the buffer pool and its page-level checksum is zero
/// ("never stamped"): a slot write rewrites only the slot's own bytes, so the identity block
/// and each slot carry their own checksums instead.
/// </para>
/// <para>
/// <see cref="Magic"/> and <see cref="FormatVersion"/> sit at the same offsets in every
/// storage format, so an engine reads them from the raw page before it verifies any checksum
/// and refuses a file of another format with <see cref="StorageFormatException"/>
/// rather than reporting it as corrupt.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = ByteSize, Pack = 1)]
public unsafe struct StorageFileHeader
{
    /// <summary>
    /// A magic number identifying this as a Cohesion database storage file.
    /// Value: <c>0x434F4845</c> (ASCII "COHE"). At the same offset in every storage format.
    /// </summary>
    [FieldOffset(0)]
    public int Magic;

    /// <summary>
    /// The storage format version. At the same offset in every storage format, and read
    /// before any checksum: an engine opens only <see cref="CurrentFormatVersion"/>.
    /// </summary>
    [FieldOffset(4)]
    public int FormatVersion;

    /// <summary>
    /// The page size in bytes used throughout this storage file.
    /// Typically <c>8192</c>.
    /// </summary>
    [FieldOffset(8)]
    public int PageSize;

    /// <summary>
    /// The <see cref="StorageModel"/> identifying which database model this file serves
    /// (SQL, Document, KeyValuePair, Graph, etc.).
    /// </summary>
    [FieldOffset(12)]
    public StorageModel Model;

    /// <summary>
    /// The unique identifier for this storage resource, stored as a 16-byte GUID.
    /// </summary>
    [FieldOffset(16)]
    public fixed byte StorageId[16];

    /// <summary>
    /// A timestamp representing when the storage file was created, stored as UTC ticks.
    /// </summary>
    [FieldOffset(32)]
    public long CreatedAtUtcTicks;

    /// <summary>
    /// The name of the storage resource, stored as a fixed-length UTF-8 encoded string.
    /// Maximum 128 bytes (padded with null bytes).
    /// </summary>
    [FieldOffset(40)]
    public fixed byte Name[128];

    /// <summary>
    /// The CRC-32C of the <see cref="ByteSize"/> bytes of this block, computed with this
    /// field treated as zero.
    /// </summary>
    [FieldOffset(168)]
    public uint Checksum;

    /// <summary>
    /// Reserved bytes for future use; zero.
    /// </summary>
    [FieldOffset(172)]
    public fixed byte Reserved[84];

    /// <summary>
    /// The size of the identity block in bytes: it occupies the first
    /// <see cref="ByteSize"/> bytes of page 0's body.
    /// </summary>
    public const int ByteSize = 256;

    /// <summary>
    /// The expected magic number value for valid Cohesion storage files.
    /// ASCII encoding of "COHE".
    /// </summary>
    public const int ExpectedMagic = 0x434F4845;

    /// <summary>
    /// The current storage format version, the only one this engine opens. Version 2
    /// (#1251) brought CRC-32C page and journal checksums, journal frame version 3, the
    /// alternating header slots, the persisted LSN floor and the chained checkpoint anchor.
    /// There is no upgrade path between versions (#1152).
    /// </summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>
    /// Validates that the header names this engine's file format: the expected magic number
    /// and exactly <see cref="CurrentFormatVersion"/>.
    /// </summary>
    /// <returns><c>true</c> if the header is valid; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsValid()
    {
        return Magic == ExpectedMagic && FormatVersion == CurrentFormatVersion;
    }
}
