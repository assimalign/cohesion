namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Raised when a storage file set is in an on-disk format this engine does not read: a
/// data file whose header names another storage format version
/// (<see cref="StorageFileHeader.CurrentFormatVersion"/>), or a journal frame that passes
/// its checksum but carries another journal frame version.
/// </summary>
/// <remarks>
/// <para>
/// The data file's format is checked first, from the raw bytes of page 0 and before any
/// checksum is verified, so an older or newer file is refused as a format mismatch rather
/// than reported as corrupt: the checksum algorithm itself is part of the format, and a
/// file written with another one would otherwise fail its checksum first. PostgreSQL reads
/// <c>pg_control</c> the same way, version before CRC, because "complaining about wrong
/// version will probably be more enlightening than complaining about wrong CRC"
/// (<c>src/backend/access/transam/xlog.c</c>, <c>ReadControlFile</c>).
/// </para>
/// <para>
/// There is no upgrade path between storage formats (on-disk format upgrades are tracked by
/// assimalign/cohesion#1152). The message leads with <see cref="ErrorCode"/> and names the
/// format found, the format supported and the remedy.
/// </para>
/// </remarks>
public sealed class StorageFormatException : StorageException
{
    /// <summary>
    /// The code that leads the message of every refusal: a storage file set in an on-disk
    /// format this engine does not read.
    /// </summary>
    public const string ErrorCode = "COHDBS001";

    private StorageFormatException(string message, int foundVersion, int supportedVersion)
        : base(message)
    {
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>
    /// Gets the format version found on disk: a storage format version
    /// (<see cref="StorageFileHeader.FormatVersion"/>) when page 0 of a data file was refused,
    /// or a journal frame format version when a journal frame was refused (the message says
    /// which).
    /// </summary>
    /// <remarks>
    /// The two version series are separate: storage format 2 writes journal frame format 3,
    /// and storage format 1 wrote journal frame format 2.
    /// </remarks>
    public int FoundVersion { get; }

    /// <summary>
    /// Gets the only format version this engine reads, in the same series as
    /// <see cref="FoundVersion"/>: <see cref="StorageFileHeader.CurrentFormatVersion"/> for a
    /// refused data file, the current journal frame format (3) for a refused journal frame.
    /// </summary>
    public int SupportedVersion { get; }

    /// <summary>
    /// Creates the refusal of a data file whose header names another storage format.
    /// </summary>
    /// <param name="foundVersion">The storage format version page 0 names.</param>
    internal static StorageFormatException ForDataFile(int foundVersion)
    {
        int supported = StorageFileHeader.CurrentFormatVersion;
        string remedy = foundVersion < supported
            ? "This engine does not upgrade storage files: export the data with the engine that wrote it, drop the database " +
              "and create it again with this engine (on-disk format upgrades are tracked by assimalign/cohesion#1152)."
            : "The file was written by a newer engine; open it with that engine (on-disk format upgrades are tracked by " +
              "assimalign/cohesion#1152).";

        return new StorageFormatException(
            $"{ErrorCode}: The storage file uses storage format {foundVersion}, but this engine supports only storage format {supported}. {remedy}",
            foundVersion,
            supported);
    }

    /// <summary>
    /// Creates the refusal of a journal frame whose checksum verifies but whose frame
    /// version this engine does not read.
    /// </summary>
    /// <param name="frameNumber">The frame's position in the journal, counting from 1.</param>
    /// <param name="foundVersion">The frame version found.</param>
    /// <param name="supportedVersion">The only frame version this engine reads.</param>
    internal static StorageFormatException ForJournalFrame(long frameNumber, int foundVersion, int supportedVersion)
    {
        string remedy = foundVersion < supportedVersion
            ? "This engine does not upgrade journals: recover the file set with the engine that wrote it, which empties the journal " +
              "at its clean close (on-disk format upgrades are tracked by assimalign/cohesion#1152)."
            : "The journal was written by a newer engine; open the file set with that engine (on-disk format upgrades are tracked by " +
              "assimalign/cohesion#1152).";

        return new StorageFormatException(
            $"{ErrorCode}: Journal frame {frameNumber} uses journal frame format {foundVersion}, but this engine reads only " +
            $"journal frame format {supportedVersion}; it passed its checksum, so it is not a torn tail. {remedy}",
            foundVersion,
            supportedVersion);
    }
}
