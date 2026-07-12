using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Defines the binary layout of the file metadata stored in the body of page 0.
/// The file header contains metadata that identifies the storage resource and
/// describes its configuration.
/// </summary>
/// <remarks>
/// Every storage file, regardless of database model, begins with a file header page
/// (<see cref="PageType.FileHeader"/>). Page 0 carries a normal page header (identifier,
/// LSN, checksum) in its first <see cref="Units.Page.HeaderSize"/> bytes, so it is
/// integrity-checked like every other page; this struct lives in the page body
/// immediately after it.
/// <code>
/// File Layout:
/// ┌─────────────────────────────────┐  Page 0
/// │ Page header │ StorageFileHeader │
/// ├─────────────────────────────────┤  Page 1..N
/// │ Data / Index / Catalog pages    │
/// └─────────────────────────────────┘
/// </code>
/// Free pages are stamped <see cref="PageType.Free"/> in their page headers; the
/// free-space map is reconstructed by scanning page headers when the file is opened.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 256, Pack = 1)]
public unsafe struct StorageFileHeader
{
    /// <summary>
    /// A magic number identifying this as a Cohesion database storage file.
    /// Value: <c>0x434F4845</c> (ASCII "COHE").
    /// </summary>
    [FieldOffset(0)]
    public int Magic;

    /// <summary>
    /// The storage format version, allowing forward/backward compatibility detection.
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
    /// The total number of pages currently in the storage file (including free pages).
    /// </summary>
    [FieldOffset(32)]
    public long TotalPageCount;

    /// <summary>
    /// The number of pages currently marked as free.
    /// </summary>
    [FieldOffset(40)]
    public long FreePageCount;

    /// <summary>
    /// The page identifier of the first free space map page.
    /// </summary>
    [FieldOffset(48)]
    public long FreeSpaceMapPageId;

    /// <summary>
    /// The page identifier of the root segment page for this storage file.
    /// </summary>
    [FieldOffset(56)]
    public long RootSegmentPageId;

    /// <summary>
    /// A timestamp representing when the storage file was created, stored as UTC ticks.
    /// </summary>
    [FieldOffset(64)]
    public long CreatedAtUtcTicks;

    /// <summary>
    /// A timestamp representing the last modification of the storage file, stored as UTC ticks.
    /// </summary>
    [FieldOffset(72)]
    public long ModifiedAtUtcTicks;

    /// <summary>
    /// The name of the storage resource, stored as a fixed-length UTF-8 encoded string.
    /// Maximum 128 bytes (padded with null bytes).
    /// </summary>
    [FieldOffset(80)]
    public fixed byte Name[128];

    /// <summary>
    /// The log sequence number of the most recent completed checkpoint, or zero when
    /// no checkpoint has been taken. Recovery replays the journal from this point.
    /// </summary>
    [FieldOffset(208)]
    public long LastCheckpointLsn;

    /// <summary>
    /// Reserved bytes for future use.
    /// </summary>
    [FieldOffset(216)]
    public fixed byte Reserved[40];

    /// <summary>
    /// The expected magic number value for valid Cohesion storage files.
    /// ASCII encoding of "COHE".
    /// </summary>
    public const int ExpectedMagic = 0x434F4845;

    /// <summary>
    /// The current format version.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// Validates that the header contains a valid magic number and format version.
    /// </summary>
    /// <returns><c>true</c> if the header is valid; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsValid()
    {
        return Magic == ExpectedMagic && FormatVersion > 0;
    }
}
