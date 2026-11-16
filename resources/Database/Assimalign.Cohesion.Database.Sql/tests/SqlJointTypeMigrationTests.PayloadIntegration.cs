using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint page walkers count payload leaves without reading bit 15 as separator identity (#1778, #1809)")]
    [InlineData(3)]
    [InlineData(120)]
    public async Task CountJointIndexPages_PayloadTree_ShouldCountPagesAndHeight(int entries)
    {
        // Arrange: use the kernel's real payload writer on the provisioner's own storage.
        await using var engine = SqlDatabaseEngine.Create("joint-payload", new());
        var database = await engine.CreateDatabaseAsync("typed", CancellationToken.None);
        var locks = new LockManager();
        var transactions = TransactionManager.Create(locks, VersionStore.CreateInMemory());
        var context = await transactions.BeginAsync(cancellationToken: CancellationToken.None);
        using var bracket = database.DataStorage.BeginTransaction();
        var manager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        { Storage = database.DataStorage, LockManager = locks, TransactionSource = _ => bracket });
        const ulong owner = 4242;
        var tree = await manager.CreateIndexAsync(context, owner, new IndexDefinition("payload"));
        byte[] payload = new byte[1024];
        for (int i = 0; i < entries; i++) { await tree.InsertAsync(context, IndexKey.FromInt64(i), (ulong)i, payload); }
        bracket.Commit(); await transactions.CommitAsync(context, CancellationToken.None);
        var pages = new HashSet<long>(); long leafBytes = 0; int flagged = 0;
        for (long pageId = 1; pageId < database.DataStorage.PageManager.PageCount; pageId++)
        {
            using var page = database.DataStorage.PageManager.GetPage((PageId)pageId);
            if (page.Page.OwnerId != owner || page.Page.Type != PageType.Index) { continue; }
            pages.Add(pageId); var body = page.Page.AsBodySpan();
            if (body[3] != 1) { continue; }
            int count = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            leafBytes += body.Length - BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) + 2 * count;
            for (int i = 0; i < count; i++)
            {
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(body[(32 + 2 * i)..]);
                (BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]) & 0x8000).ShouldBe(0x8000); flagged++;
            }
        }
        // Act
        var visited = new HashSet<long>();
        long root = manager.ExportRegistrations().Single().RootPageId;
        var measured = CountPayloadPages(database.SchemaProvisioner, root, owner, CancellationToken.None, visited);
        int height = SqlSchemaProvisioner.JointTreeHeight(database.DataStorage, root, owner, out int free);
        // Assert
        flagged.ShouldBe(entries); measured.Pages.ShouldBe(pages.Count); visited.SetEquals(pages).ShouldBeTrue();
        measured.Bytes.ShouldBe(leafBytes); measured.Height.ShouldBe(entries == 3 ? 1 : 2);
        height.ShouldBe(measured.Height); free.ShouldBeGreaterThanOrEqualTo(0);
        _output.WriteLine($"JOINT_PAYLOAD_WALK|entries={entries}|pages={measured.Pages}|height={height}|leafBytes={leafBytes}");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CountJointIndexPages")]
    private static extern (long Pages, int Height, long Bytes) CountPayloadPages(SqlSchemaProvisioner provisioner,
        long root, ulong owner, CancellationToken token, HashSet<long>? registered);
}
