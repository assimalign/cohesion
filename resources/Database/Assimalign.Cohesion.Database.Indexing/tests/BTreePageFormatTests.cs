using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// The B-tree page format gate (#1194): every node page carries a magic and the page
/// format version, the index manager checks each existing tree's root when it attaches
/// it, and a tree in any other format — format 1, written before the entry order
/// changed, or a newer one — is refused with <c>COHDBI001</c> instead of misread. A
/// page that is not a current-format node, reached inside an attached tree, fails the
/// operation with <see cref="IndexCorruptionException"/> (<c>COHDBI002</c>).
/// </summary>
public class BTreePageFormatTests
{
    private static async Task<(IndexTestHarness Harness, IReadOnlyList<BTreeIndexRegistration> Registrations)> CreateTreeAsync(int entries)
    {
        var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 9, new IndexDefinition("ix_format"));
        for (long i = 0; i < entries; i++)
        {
            await index.InsertAsync(setup, IndexKey.FromInt64(i), (ulong)i);
        }
        await harness.CommitAsync(setup);
        return (harness, ((IIndexRegistry)harness.IndexManager).ExportRegistrations());
    }

    private static void RewritePage(IStorage storage, long pageId, LegacyBTreePages.BodyRewriter rewrite)
    {
        using var bracket = storage.BeginTransaction();
        using (var handle = storage.OpenPageForWrite(bracket, pageId))
        {
            rewrite(handle.Page.AsBodySpan());
            handle.MarkDirty();
        }

        bracket.Commit();
    }

    private static IIndexManager Attach(IndexTestHarness harness, IReadOnlyList<BTreeIndexRegistration> registrations)
        => BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = harness.Storage,
            TransactionSource = harness,
            LockManager = harness.LockManager,
            ExistingIndexes = registrations,
        });

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Page format: new trees are stamped with format 2 and reattach")]
    public async Task Attach_CurrentFormat_ShouldSucceed()
    {
        // Arrange: a tree several leaves wide.
        var (harness, registrations) = await CreateTreeAsync(2_000);
        await using var harnessLifetime = harness;

        // Act
        var manager = Attach(harness, registrations);

        // Assert
        BTreeIndexManager.FormatVersion.ShouldBe(2);
        manager.TryGetIndex(9, "ix_format", out var index).ShouldBeTrue();
        var reader = await harness.BeginAsync();
        int count = 0;
        await using (var cursor = index.OpenCursor(reader, IndexKeyRange.All))
        {
            while (await cursor.MoveNextAsync())
            {
                count++;
            }
        }
        count.ShouldBe(2_000);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Page format: a tree whose root is in format 1 is refused at attach with COHDBI001")]
    [InlineData(10)]     // the root is a leaf
    [InlineData(2_000)]  // the root is an internal node
    public async Task Attach_RootInFormat1_ShouldRefuse(int entries)
    {
        // Arrange: the root page rewritten into the layout engines before #1194 wrote.
        var (harness, registrations) = await CreateTreeAsync(entries);
        await using var harnessLifetime = harness;
        long root = registrations.Single().RootPageId;
        RewritePage(harness.Storage, root, LegacyBTreePages.DowngradeToFormat1);

        // Act
        var refusal = Should.Throw<IndexFormatException>(() => Attach(harness, registrations));

        // Assert
        refusal.Message.ShouldStartWith(IndexFormatException.ErrorCode + ":", Case.Sensitive);
        refusal.Message.ShouldContain("uses B-tree page format 1, but this engine supports only format 2", Case.Sensitive);
        refusal.Message.ShouldContain("#1152");
        refusal.FoundVersion.ShouldBe(1);
        refusal.SupportedVersion.ShouldBe(2);
        refusal.IndexName.ShouldBe("ix_format");
        refusal.ObjectId.ShouldBe(9UL);
        refusal.RootPageId.ShouldBe(root);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Page format: a tree written in a newer format is refused at attach")]
    public async Task Attach_RootInNewerFormat_ShouldRefuse()
    {
        // Arrange
        var (harness, registrations) = await CreateTreeAsync(10);
        await using var harnessLifetime = harness;
        RewritePage(harness.Storage, registrations.Single().RootPageId, body => LegacyBTreePages.StampVersion(body, 3));

        // Act
        var refusal = Should.Throw<IndexFormatException>(() => Attach(harness, registrations));

        // Assert
        refusal.FoundVersion.ShouldBe(3);
        refusal.Message.ShouldContain("uses B-tree page format 3, but this engine supports only format 2", Case.Sensitive);
        refusal.Message.ShouldContain("newer engine");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Page format: a registration that points at no B-tree page is refused at attach")]
    public async Task Attach_RootNotABTreePage_ShouldRefuse()
    {
        // Arrange
        var (harness, registrations) = await CreateTreeAsync(10);
        await using var harnessLifetime = harness;
        RewritePage(harness.Storage, registrations.Single().RootPageId, body => body.Fill(0xA7));

        // Act
        var refusal = Should.Throw<IndexFormatException>(() => Attach(harness, registrations));

        // Assert
        refusal.FoundVersion.ShouldBe(0);
        refusal.Message.ShouldContain("not a B-tree page of any known format");
        refusal.Message.ShouldContain("damaged");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Page format: a current stamp over a damaged node header is refused as damage, not as a format")]
    public async Task Attach_CurrentStampOverDamagedKind_ShouldRefuseAsDamaged()
    {
        // Arrange: the magic and version are intact, the kind byte is not.
        var (harness, registrations) = await CreateTreeAsync(10);
        await using var harnessLifetime = harness;
        RewritePage(harness.Storage, registrations.Single().RootPageId, body => body[3] = 9);

        // Act
        var refusal = Should.Throw<IndexFormatException>(() => Attach(harness, registrations));

        // Assert
        refusal.FoundVersion.ShouldBe(0);
        refusal.Message.ShouldContain("not a B-tree page of any known format");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Page format: EnsureFormat checks registrations without attaching or writing")]
    public async Task EnsureFormat_Registrations_ShouldCheckEachRoot()
    {
        // Arrange: two trees, both current.
        var harness = new IndexTestHarness();
        await using var harnessLifetime = harness;
        var setup = await harness.BeginAsync();
        await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_first"));
        await harness.IndexManager.CreateIndexAsync(setup, 2, new IndexDefinition("ix_second"));
        await harness.CommitAsync(setup);
        var registrations = ((IIndexRegistry)harness.IndexManager).ExportRegistrations();

        // Act / Assert: current trees pass; once one is in format 1, it is named.
        BTreeIndexManager.EnsureFormat(harness.Storage, registrations);
        RewritePage(harness.Storage, registrations.Single(registration => registration.ObjectId == 2).RootPageId, LegacyBTreePages.DowngradeToFormat1);
        Should.Throw<IndexFormatException>(() => BTreeIndexManager.EnsureFormat(harness.Storage, registrations))
            .IndexName.ShouldBe("ix_second");
        LegacyBTreePages.DowngradeIndexPages(harness.Storage).ShouldBe(1); // the other root: the only format-2 node left
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Page format: one refused tree refuses the attach of every tree")]
    public async Task Attach_OneOfSeveralTreesRefused_ShouldAttachNone()
    {
        // Arrange: two trees; the second's root is in format 1.
        var harness = new IndexTestHarness();
        await using var harnessLifetime = harness;
        var setup = await harness.BeginAsync();
        await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_current"));
        await harness.IndexManager.CreateIndexAsync(setup, 2, new IndexDefinition("ix_legacy"));
        await harness.CommitAsync(setup);
        var registrations = ((IIndexRegistry)harness.IndexManager).ExportRegistrations();
        RewritePage(harness.Storage, registrations.Single(registration => registration.ObjectId == 2).RootPageId, LegacyBTreePages.DowngradeToFormat1);

        // Act
        var refusal = Should.Throw<IndexFormatException>(() => Attach(harness, registrations));

        // Assert
        refusal.IndexName.ShouldBe("ix_legacy");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Page format: a damaged node inside an attached tree fails the lookup and the delete with IndexCorruptionException (COHDBI002)")]
    public async Task Read_DamagedLeaf_ShouldFailWithCorruption()
    {
        // Arrange: a tree with an internal root; one of its leaves loses its stamp.
        var (harness, registrations) = await CreateTreeAsync(2_000);
        await using var harnessLifetime = harness;
        harness.IndexManager.TryGetIndex(9, "ix_format", out var index).ShouldBeTrue();

        long leaf = LeftmostLeaf(harness.Storage, registrations.Single().RootPageId);
        RewritePage(harness.Storage, leaf, LegacyBTreePages.DowngradeToFormat1);

        // Act
        var reader = await harness.BeginAsync();
        var failure = Should.Throw<IndexCorruptionException>(() => index.OpenCursor(reader, IndexKeyRange.All));
        var deleting = await harness.BeginAsync();
        var deleteFailure = await Should.ThrowAsync<IndexCorruptionException>(async () => await index.DeleteAsync(deleting, IndexKey.FromInt64(0), 0));
        await harness.RollbackAsync(deleting);

        // Assert: a typed failure that names the index, the page and what the page holds.
        failure.Message.ShouldStartWith(IndexCorruptionException.ErrorCode + ":", Case.Sensitive);
        failure.Message.ShouldContain($"page {leaf}");
        failure.Message.ShouldContain("found format 1");
        failure.IndexName.ShouldBe("ix_format");
        failure.PageId.ShouldBe(leaf);
        failure.FoundVersion.ShouldBe(1);
        failure.ShouldBeAssignableTo<IndexException>();
        deleteFailure.PageId.ShouldBe(leaf);
    }

    private static long LeftmostLeaf(IStorage storage, long root)
    {
        long current = root;
        while (true)
        {
            using var handle = storage.PageManager.GetPage(current);
            var body = handle.Page.AsBodySpan();
            if (body[3] == 1)
            {
                return current;
            }

            current = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(body[24..]);
        }
    }
}
