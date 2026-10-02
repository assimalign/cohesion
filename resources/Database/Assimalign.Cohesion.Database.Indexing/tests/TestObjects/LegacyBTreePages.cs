using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

/// <summary>
/// Rewrites B-tree node pages into layouts this engine must refuse, for the old-format
/// refusal tests (#1194). Linked into each model's test project, so every model proves
/// it refuses a database whose index pages are not in the current page format.
/// </summary>
/// <remarks>
/// The offsets are the documented page layouts: the storage page header (96 bytes,
/// checksum at 16, page type at 21, <c>PageType.Index</c> = 2) and the B-tree node
/// body, format 2 (<c>"BT"</c> magic at 0, version at 2, kind at 3, entry count at 4,
/// data start at 6, next leaf at 8, previous leaf at 16, leftmost child at 24,
/// directory at 32) and format 1 (kind at 0, entry count at 1, data start at 3, next
/// leaf at 5, previous leaf at 13, leftmost child at 21, directory at 29). They are
/// spelled out here, not shared with the engine, so a layout change the tests did not
/// expect fails them.
/// </remarks>
public static class LegacyBTreePages
{
    /// <summary>The storage page size.</summary>
    public const int PageSize = 8192;

    /// <summary>The storage page header size; the node body follows it.</summary>
    public const int PageHeaderSize = 96;

    private const int checksumOffset = 16;
    private const int pageTypeOffset = 21;
    private const byte indexPageType = 2;

    /// <summary>
    /// Rewrites a node body in place.
    /// </summary>
    public delegate void BodyRewriter(Span<byte> body);

    /// <summary>
    /// Gets a value indicating whether <paramref name="body"/> is a format-2 node.
    /// </summary>
    public static bool IsFormat2Node(ReadOnlySpan<byte> body)
        => body[0] == (byte)'B' && body[1] == (byte)'T' && body[2] == 2;

    /// <summary>
    /// Rewrites a format-2 node body into the format-1 layout: the header written by
    /// engines before #1194, which began with the node kind and had no magic or
    /// version. The entry bytes stay where they are; their offsets are relative to the
    /// body, so a leaf, and an internal node whose separators keep no tiebreaker, are
    /// then exactly the format-1 node an older engine wrote.
    /// </summary>
    public static void DowngradeToFormat1(Span<byte> body)
    {
        if (!IsFormat2Node(body))
        {
            throw new InvalidOperationException("The page is not a format-2 B-tree node.");
        }

        byte kind = body[3];
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        ushort dataStart = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
        long next = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
        long previous = BinaryPrimitives.ReadInt64LittleEndian(body[16..]);
        long leftmost = BinaryPrimitives.ReadInt64LittleEndian(body[24..]);
        byte[] directory = body.Slice(32, 2 * count).ToArray();

        body[..(32 + 2 * count)].Clear();
        body[0] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(body[1..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(body[3..], dataStart);
        BinaryPrimitives.WriteInt64LittleEndian(body[5..], next);
        BinaryPrimitives.WriteInt64LittleEndian(body[13..], previous);
        BinaryPrimitives.WriteInt64LittleEndian(body[21..], leftmost);
        directory.CopyTo(body[29..]);
    }

    /// <summary>
    /// Stamps a format-2 node body with another format version, as a newer engine
    /// would.
    /// </summary>
    public static void StampVersion(Span<byte> body, byte version)
    {
        if (!IsFormat2Node(body))
        {
            throw new InvalidOperationException("The page is not a format-2 B-tree node.");
        }

        body[2] = version;
    }

    /// <summary>
    /// Applies <paramref name="rewrite"/> to every format-2 node page of a closed data
    /// file and clears each rewritten page's checksum (a zero checksum reads as never
    /// stamped, so the storage layer loads the page without verifying it).
    /// </summary>
    /// <returns>The number of pages rewritten.</returns>
    public static int RewriteIndexPages(string dataFilePath, BodyRewriter rewrite)
    {
        byte[] file = File.ReadAllBytes(dataFilePath);
        int rewritten = 0;

        for (int offset = 0; offset + PageSize <= file.Length; offset += PageSize)
        {
            var page = file.AsSpan(offset, PageSize);
            if (page[pageTypeOffset] != indexPageType || !IsFormat2Node(page[PageHeaderSize..]))
            {
                continue;
            }

            rewrite(page[PageHeaderSize..]);
            page.Slice(checksumOffset, 4).Clear();
            rewritten++;
        }

        File.WriteAllBytes(dataFilePath, file);
        return rewritten;
    }

    /// <summary>
    /// Rewrites every format-2 node page of an open storage into the format-1 layout,
    /// in one committed storage transaction (the page writes are journaled like any
    /// other, so the storage stays consistent).
    /// </summary>
    /// <returns>The number of pages rewritten.</returns>
    public static int DowngradeIndexPages(IStorage storage)
    {
        int rewritten = 0;
        using var bracket = storage.BeginTransaction();

        for (long pageId = 1; pageId < storage.PageManager.PageCount; pageId++)
        {
            bool candidate;
            using (var handle = storage.PageManager.GetPage(pageId))
            {
                candidate = handle.Page.Type == PageType.Index && IsFormat2Node(handle.Page.AsBodySpan());
            }

            if (!candidate)
            {
                continue;
            }

            using var writable = storage.OpenPageForWrite(bracket, pageId);
            DowngradeToFormat1(writable.Page.AsBodySpan());
            writable.MarkDirty();
            rewritten++;
        }

        bracket.Commit();
        return rewritten;
    }

    /// <summary>
    /// Rewrites every B-tree node page of every data file (<c>*.dat</c>) under
    /// <paramref name="rootPath"/> into the format-1 layout — a database as an engine
    /// before #1194 wrote it, as far as its indexes go.
    /// </summary>
    /// <returns>The number of pages rewritten.</returns>
    public static int DowngradeDataFiles(string rootPath)
    {
        int rewritten = 0;

        foreach (string path in Directory.GetFiles(rootPath, "*.dat", SearchOption.AllDirectories))
        {
            rewritten += RewriteIndexPages(path, DowngradeToFormat1);
        }

        return rewritten;
    }
}
