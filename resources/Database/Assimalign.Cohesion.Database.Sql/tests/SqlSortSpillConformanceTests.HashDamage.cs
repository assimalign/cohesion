using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash records: codec-valid mismatched ordinal, key and state tags are located CK05004 (#1789)")]
    [InlineData(41, DatabaseType.Float64)] // ordinal: Int64
    [InlineData(50, DatabaseType.Date)] // key: Int32
    [InlineData(55, DatabaseType.Int8)] // acceptance flag: Boolean
    [InlineData(57, DatabaseType.Date)] // state argument: Int32
    [InlineData(55, DatabaseType.Float64, true)] // completed COUNT state: Int64
    [InlineData(-1, DatabaseType.Null, true)] // completed COUNT state is never NULL
    public async Task ExecuteAsync_HashRecordTypeMismatch_ShouldLocateDamage(int offset, DatabaseType tag, bool summary = false)
    {
        // Arrange: change a sealed partition record without invalidating its framing or CRC.
        await using var engine = await Create("hash-tag-damage", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000);
        bool injected = false, armed = !summary;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase == "partitioned") { armed = true; } };
        BeforeRead(engine.TempSpace!, () =>
        {
            if (injected || !armed) { return; }
            var run = StatementRuns(engine.TempSpace!).First(r => r.IsSealed && r.RecordCount > 0);
            var page = new byte[8192];
            RandomAccess.Read(Handle(run), page, 0);
            if (offset >= 0) { page[offset] = (byte)tag; }
            else
            {
                // Widen both numeric keys by four bytes and replace COUNT's eight-byte
                // payload with NULL: valid codec, identical total record length.
                int length = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(32));
                SqlTempRowCodec.TryDecode(page.AsSpan(36, length), out var row).ShouldBeTrue();
                row[1] = (long)row[1].ShouldBeOfType<int>(); row[2] = (long)row[2].ShouldBeOfType<int>(); row[3] = null;
                SqlTempRowCodec.TryEncode(row, out var bytes).ShouldBeTrue(); bytes.Length.ShouldBe(length);
                bytes.CopyTo(page, 36);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28), 0);
            uint crc = uint.MaxValue;
            foreach (byte value in page)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82f63b78u); }
            }
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28), ~crc);
            RandomAccess.Write(Handle(run), page, 0); injected = true;
        });
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(offset < 0 ? "SELECT id,COUNT(*) FROM t GROUP BY id,CAST(id+1 AS INT)" :
                    summary ? "SELECT id,COUNT(*) FROM t GROUP BY id" : "SELECT id,MIN(CAST(id AS INT)) FROM t GROUP BY id"));

            // Assert
            injected.ShouldBeTrue(); error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK05004");
            error.Diagnostics[0].Start.ShouldNotBeNull(); error.Diagnostics[0].End.ShouldNotBeNull();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
        }
        finally { BeforeRead(engine.TempSpace!, null); SqlPlanExecutor.ObserveHashSpill = null; }
    }
}
