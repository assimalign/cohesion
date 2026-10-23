using System;
using System.IO;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The one database-name rule and the one mapping from a database's name to its files, shared by
/// every model (#1392): which names a model stores, the directory each name maps to under an
/// engine's root path, the file sets inside that directory, and the name an opened database reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> A name is 1 to <see cref="MaximumLength"/> characters, each an ASCII letter,
/// digit, underscore, hyphen or dot. It neither starts nor ends with a dot and holds no two dots in
/// a row, and the part before its first dot is not a Windows device name (CON, PRN, AUX, NUL, COM0
/// to COM9, LPT0 to LPT9; Windows resolves <c>NUL.txt</c> to <c>NUL</c> too). The allowed
/// characters are a fixed set, not the platform's invalid file-name characters: on Linux and macOS
/// <see cref="Path.GetInvalidFileNameChars"/> holds only NUL and <c>/</c>, so a name such as
/// <c>a:b</c> was accepted there and refused on Windows. Now a name valid on one operating system is
/// valid on every one, and every valid name is a legal directory name on each. The extra characters
/// and the length follow RavenDB (<c>src/Raven.Client/Util/ResourceNameValidator.cs:12-14</c> and
/// <c>:55</c>, <c>MaxDatabaseNameLength</c> at <c>src/Raven.Client/Constants.cs:443</c>), and so
/// does the dot placement (<c>ResourceNameValidator.cs:25</c>). The letters and digits narrow
/// RavenDB's, which are any Unicode letter or digit (<c>char.IsLetterOrDigit</c>,
/// <c>ResourceNameValidator.cs:118</c>), to ASCII, so that lower-casing a name is the same
/// relation as <see cref="DatabaseName"/>'s ordinal ignore-case equality (below). The device names
/// extend RavenDB's list (<c>Constants.cs:278-302</c>) with COM0 and LPT0, which Windows reserves
/// too, and apply to the part before the first dot, where RavenDB compares the whole name
/// (<c>ResourceNameValidator.cs:65</c>). The 128 limit is also the name capacity of a storage
/// header, so the header keeps every valid name whole (<see cref="GetOpenedName"/>).
/// </para>
/// <para>
/// <b>The mapping.</b> <see cref="DatabaseName"/> compares ordinally, ignoring case. A database's
/// directory is its storage name (<see cref="GetStorageName"/>), the invariant lower case of its
/// name, under the engine's root path. For ASCII, lower-case equality and ordinal ignore-case
/// equality are the same relation, so two names <see cref="DatabaseName"/> calls equal map to one
/// directory and two unequal names never share one, on case-sensitive file systems (Linux, the
/// container target) as on case-insensitive ones. The mapping is a pure function of the name.
/// Nothing enumerates the root to find a directory, and listing accepts only lower-case valid names
/// (<see cref="IsStorageName"/>). A case-variant directory made outside the engine, <c>Sales</c>
/// for <c>sales</c>, is outside the contract and behaves as its file system makes it: Linux ignores
/// it, while on Windows and macOS the file system resolves the lower-case path to it, so an open
/// finds it and a create refuses it. Neo4j folds database names the same way
/// (<c>community/common/src/main/java/org/neo4j/kernel/database/NormalizedDatabaseName.java:27-29</c>);
/// PostgreSQL keeps names out of the file system altogether (a database's directory is its OID,
/// <c>src/common/relpath.c:110-121</c>).
/// </para>
/// <para>
/// <b>File sets.</b> A database's file sets live in its directory, each as <c>{set}.dat</c>,
/// <c>{set}.log</c> and <c>{set}.bak</c> (<see cref="GetFiles"/>): the model's data set, named for
/// the model, and for SQL and key-value the <see cref="CatalogFileSet"/> set. No file set is named
/// from the database name, so a file set of one database is never the directory of another. Until
/// #1392 the SQL and key-value catalog of <c>sales</c> was the directory <c>sales.catalog</c>, the
/// directory of a database by that name. In memory, an engine keys each file set by storage name and
/// set name (<see cref="DatabaseMemoryFiles"/>).
/// </para>
/// <para>
/// <b>The reported name.</b> A created database reports the name it was created with. An opened one
/// reports the spelling its data file set's storage header kept from its creation
/// (<see cref="GetOpenedName"/>), so <c>sales</c> opened as <c>SALES</c> still reports
/// <c>sales</c>, in every engine and after every restart. The name a database reports never depends
/// on the spelling a caller opened it with.
/// </para>
/// <para>
/// Compiled into each model assembly from the root's <c>shared</c> folder, as
/// <see cref="DatabaseDeclarations"/> is. Each engine checks the rule in its create, open and drop
/// cores, after the root base's checks of an empty name, disposal and the token, so the guard order
/// of the public members is unchanged; each engine builder also checks it when a database is
/// declared, so a declaration the engine would refuse fails at the call, before anything is created.
/// </para>
/// </remarks>
internal static class DatabaseFileNames
{
    /// <summary>
    /// The longest database name, in characters: RavenDB's limit, and the name capacity of a storage
    /// header, in bytes, which an ASCII name fills one byte per character.
    /// </summary>
    internal const int MaximumLength = 128;

    /// <summary>
    /// The rule, as every refusal of a name starts, the same on every model and operating system.
    /// </summary>
    internal static string Rule => EngineErrorMessages.DatabaseNameRule;

    /// <summary>
    /// The file set that holds a SQL or key-value database's catalog, in the database's directory.
    /// </summary>
    internal const string CatalogFileSet = "catalog";

    /// <summary>
    /// Says why a name breaks the rule.
    /// </summary>
    /// <param name="name">The database name.</param>
    /// <returns>The reason, or null when the name follows the rule.</returns>
    internal static string? GetRefusal(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return EngineErrorMessages.DatabaseNameEmpty;
        }

        if (name.Length > MaximumLength)
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, EngineErrorMessages.DatabaseNameTooLong, name.Length);
        }

        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, EngineErrorMessages.DatabaseNameCharacterInvalid, Describe(character), index);
            }
        }

        if (name[0] == '.')
        {
            return EngineErrorMessages.DatabaseNameLeadingDot;
        }

        if (name[^1] == '.')
        {
            return EngineErrorMessages.DatabaseNameTrailingDot;
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return EngineErrorMessages.DatabaseNameConsecutiveDots;
        }

        int dot = name.IndexOf('.');
        var stem = dot < 0 ? name.AsSpan() : name.AsSpan(0, dot);
        if (IsDeviceName(stem))
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, EngineErrorMessages.DatabaseNameDeviceReserved, stem.ToString());
        }

        return null;
    }

    /// <summary>
    /// Refuses a database name that is null, blank, or breaks the rule.
    /// </summary>
    /// <param name="name">The database name.</param>
    /// <param name="paramName">The parameter the name was passed as.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space, or breaks the rule; the message starts with
    /// <see cref="Rule"/> and ends with the reason.
    /// </exception>
    internal static void ThrowIfInvalid(string name, string paramName = "name")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        if (GetRefusal(name) is { } reason)
        {
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, EngineErrorMessages.FileNamesThrowIfInvalidArgument, Rule, reason), paramName);
        }
    }

    /// <summary>
    /// Gets a database's storage name: the invariant lower case of its name, which names its
    /// directory under an engine's root path and keys its in-memory file sets.
    /// </summary>
    /// <param name="name">The database name.</param>
    /// <returns>The storage name; equal for two names exactly when <see cref="DatabaseName"/> calls them equal.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> breaks the rule.</exception>
    internal static string GetStorageName(string name)
    {
        ThrowIfInvalid(name);

        // ASCII alone: the invariant lower case lowers A to Z and nothing else.
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Reports whether a directory name under an engine's root path is the storage name of a
    /// database: a name that follows the rule and is already lower case. Listing skips any other
    /// directory, a case-variant one made outside the engine included.
    /// </summary>
    /// <param name="directoryName">The directory's name, without its path.</param>
    /// <returns>True when a database's name maps to that directory.</returns>
    internal static bool IsStorageName(string directoryName)
    {
        if (GetRefusal(directoryName) is not null)
        {
            return false;
        }

        foreach (char character in directoryName)
        {
            if (char.IsAsciiLetterUpper(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the directory of a database under an engine's root path: the root path and the
    /// database's storage name.
    /// </summary>
    /// <param name="rootPath">The engine's root path.</param>
    /// <param name="name">The database name.</param>
    /// <returns>The directory's path; the same for every spelling of the name.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> breaks the rule.</exception>
    internal static string GetDirectory(string rootPath, string name) => Path.Combine(rootPath, GetStorageName(name));

    /// <summary>
    /// Gets the files of one file set in a database's directory.
    /// </summary>
    /// <param name="directory">The database's directory (<see cref="GetDirectory"/>).</param>
    /// <param name="fileSet">The file set: the model's data set, or <see cref="CatalogFileSet"/>.</param>
    /// <returns>The data, journal and backup files' paths.</returns>
    internal static FileSetPaths GetFiles(string directory, string fileSet)
        => new(Path.Combine(directory, fileSet + ".dat"), Path.Combine(directory, fileSet + ".log"), Path.Combine(directory, fileSet + ".bak"));

    /// <summary>
    /// Words the refusal of a create whose database's directory is already there without its data
    /// set's data file. An interrupted create or drop leaves such a directory. It is no database:
    /// an open does not find it and listing skips it. But a create claims the directory, so it
    /// does not take one over; a drop deletes it.
    /// </summary>
    /// <param name="name">The database name.</param>
    /// <param name="directory">The database's directory (<see cref="GetDirectory"/>).</param>
    /// <returns>The message.</returns>
    internal static string GetLeftoverRefusal(string name, string directory)
        => $"Database '{name}' cannot be created: its directory '{directory}' is already there without a database in it, " +
            "left by an interrupted create or drop. Delete the directory, or drop the database, which deletes it, and create the database again.";

    /// <summary>
    /// Gets how a SQL or key-value database's catalog file set is named in messages and in its
    /// storage header: <c>{name}/catalog</c>. No database name holds a slash, so the label never
    /// reads as a database's name.
    /// </summary>
    /// <param name="name">The database name.</param>
    /// <returns>The label.</returns>
    internal static string GetCatalogLabel(string name) => name + "/" + CatalogFileSet;

    /// <summary>
    /// Gets the name a database an engine opened reports: the spelling its data file set's storage
    /// header kept from its creation, when that is a spelling of the name the caller opened;
    /// otherwise the storage name. Either way, every caller's spelling reports the same name.
    /// </summary>
    /// <param name="requested">The name the caller opened the database by.</param>
    /// <param name="stored">The name the data file set's storage header holds.</param>
    /// <returns>The name the opened database reports.</returns>
    /// <exception cref="ArgumentException"><paramref name="requested"/> breaks the rule.</exception>
    /// <remarks>
    /// The header keeps a different name only when the files were moved by hand, from the directory
    /// of another name: the directory, not the header, identifies the database, so it opens, under
    /// the storage name of the directory it was opened from.
    /// </remarks>
    internal static string GetOpenedName(string requested, string stored)
    {
        string storageName = GetStorageName(requested);
        return GetRefusal(stored) is null && string.Equals(stored.ToLowerInvariant(), storageName, StringComparison.Ordinal)
            ? stored
            : storageName;
    }

    // A Windows device name: CON, PRN, AUX, NUL, COM0 to COM9, LPT0 to LPT9, in any case. The
    // superscript COM¹ and LPT¹ forms are not ASCII, so the rule refuses them already.
    private static bool IsDeviceName(ReadOnlySpan<char> stem)
    {
        if (stem.Length == 3)
        {
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        return stem.Length == 4
            && char.IsAsciiDigit(stem[3])
            && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase));
    }

    // A refused character as a message shows it: itself when it is printable ASCII, else its code point.
    private static string Describe(char character)
        => character is >= '!' and <= '~' ? $"'{character}'" : $"U+{(int)character:X4}";

    /// <summary>
    /// The files of one file set: its data, journal and backup files.
    /// </summary>
    /// <param name="Data">The data file (<c>.dat</c>).</param>
    /// <param name="Journal">The journal file (<c>.log</c>).</param>
    /// <param name="Backup">The backup file (<c>.bak</c>).</param>
    internal readonly record struct FileSetPaths(string Data, string Journal, string Backup);
}
