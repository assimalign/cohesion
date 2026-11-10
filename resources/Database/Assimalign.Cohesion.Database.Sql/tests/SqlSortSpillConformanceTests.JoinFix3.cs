using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Fitting EXPLAIN keeps base text and NULL actuals (#1789)")]
    [InlineData("INNER", false)]
    [InlineData("LEFT", false)]
    [InlineData("RIGHT", false)]
    [InlineData("FULL", false)]
    [InlineData("CROSS", false)]
    [InlineData("INNER", true)]
    [InlineData("LEFT", true)]
    [InlineData("RIGHT", true)]
    [InlineData("FULL", true)]
    [InlineData("CROSS", true)]
    public async Task ExecuteAsync_JoinFix3Explain_ShouldPreserveFittingText(string kind, bool temp)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("fix3-explain"); builder.Options.StatementMemoryBudget = 262144;
        if (temp) { builder.Options.TempDirectory = Path.Combine(_root, "explain"); }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE jl(id INT)"); await session.ExecuteAsync("CREATE TABLE jr(id INT)");
        await session.ExecuteAsync("INSERT INTO jl VALUES(1),(2)"); await session.ExecuteAsync("INSERT INTO jr VALUES(1),(2)");
        string query = $"SELECT jl.id,jr.id FROM jl {kind} JOIN jr" + (kind == "CROSS" ? "" : " ON jl.id=jr.id");
        // Act / Assert
        foreach (bool analyze in new[] { false, true })
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN " + (analyze ? "ANALYZE " : "") + query);
            var rows = await Rows(result);
            string text = string.Join('\n', rows.Select(row => row.ToString()));
            _output.WriteLine($"FIX3_EXPLAIN|{kind}|analyze={analyze}|{text}");
            bool permitted = false;
#if !L3B_BASE
            permitted = temp && kind != "CROSS";
            if (analyze && kind != "CROSS") { text.ShouldContain("hash_status=fitting", Case.Sensitive); }
#endif
            text.ShouldContain("hash_budget=" + (permitted ? analyze ? "spill_on_refusal" : "spill_on_refusal_if_eligible" : "no_spill"), Case.Sensitive);
            if (kind != "CROSS") { text.ShouldContain("bounded in-memory hash", Case.Sensitive); }
            // Pin every rendered cell to base, apart from the explicit policy/status additions.
            foreach (var row in rows)
            {
                if (row.Cells[5] is string detail)
                { row.Cells[5] = detail.Replace("; hash_status=fitting", "", StringComparison.Ordinal)
                    .Replace("; hash_budget=spill_on_refusal_if_eligible", "; hash_budget=no_spill", StringComparison.Ordinal)
                    .Replace("; hash_budget=spill_on_refusal", "; hash_budget=no_spill", StringComparison.Ordinal); }
            }
            Fix2Fingerprint(rows).ShouldBe((kind, analyze) switch
            {
                ("INNER", false) => "6E643AE96CEB583C9A88CE28D9EE35058E6D7BF1B44C784F35D9744C2793D266",
                ("INNER", true) => "84DB1B390A3A1546BB8DF38B129609BD0D57E78290B5FA222250DC49628B80DF",
                ("LEFT", false) => "A570D76D9666B639035714899F530C5A6D205942FC0386FB1714A5A386C3F57B",
                ("LEFT", true) => "AAEE8A1F271B89368FC5484997496A1DF8C94884529C96DB65B025FBDA2BA325",
                ("RIGHT", false) => "E617DFC62CA5D136719CC349E6FB56FAAC7E3180CEB46CF235DE946E43AC47FB",
                ("RIGHT", true) => "9D2312F7B348F39FD789586B7468C2CDB63915B74DC17E17C90D49AD5BD9D007",
                ("FULL", false) => "44235AEB99A635B24AB75B0881D51133A73B511CE82D64C10BA29DC5FA07EAF0",
                ("FULL", true) => "A4C8A0067A78F1A2B66C6E84B3857D7B2CB8917FAF4F97CF5E02570F7FCA3D3B",
                ("CROSS", false) => "B69EC1F3E52F0E4659F8E78210D59CA0AA74324463FB57833EF3D672E2364931",
                _ => "08F09D958919753482133F82EA5B736FC32B236144768F73025212DBC4476A7A",
            });
        }
    }

#if !L3B_BASE
    [Fact(DisplayName = "Cohesion Test [Sql] - A FULL (B FULL C) yields optional memory to a starved child unmatched sort (#1789)")]
    public async Task ExecuteAsync_JoinFix3NestedFull_ShouldYieldParentArena()
    {
        // Arrange
        const long budget = 1048576;
        await using var engine = await CreateFix2("fix3-nested-sql", budget, quota: 134217728);
        await using var reference = await CreateFix2("fix3-nested-sql-reference", long.MaxValue);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await session.ExecuteAsync("CREATE TABLE ja(id INT,payload TEXT)");
            await session.ExecuteAsync("INSERT INTO ja VALUES" + string.Join(',', Enumerable.Range(0, 250).Select(i => $"({i},'{new string('x', 2500)}')")));
            await session.ExecuteAsync("CREATE TABLE jb(id INT)");
            for (int start = 0; start < 20000; start += 200)
            { await session.ExecuteAsync("INSERT INTO jb VALUES" + string.Join(',', Enumerable.Range(start, 200).Select(i => $"({i})"))); }
            await session.ExecuteAsync("CREATE TABLE jc(id INT," + string.Join(',', Enumerable.Range(0, 150).Select(i => $"c{i} CHAR(45)")) + ")");
            var random = new Random(178911);
            var values = new List<string>();
            for (int i = 0; i < 50; i++)
            {
                var cells = new List<string>();
                for (int column = 0; column < 150; column++)
                { byte[] bytes = new byte[33]; random.NextBytes(bytes); cells.Add("'" + Convert.ToBase64String(bytes) + "x'"); }
                values.Add($"({i},{string.Join(',', cells)})");
            }
            await session.ExecuteAsync("INSERT INTO jc VALUES" + string.Join(',', values));
        }
        const string sql = "SELECT a.id,b.id,c.id FROM ja a FULL JOIN (jb b FULL JOIN jc c ON b.id=c.id) ON a.id=b.id LIMIT 1";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        SqlStatementMetrics? active = null; long held = 0; bool starved = false, parent = false, yielded = false;
        int partitioned = 0;
        SqlStatementMetrics.AfterMemoryReservation = (metrics, bytes) =>
        {
            active = metrics;
            if (starved || bytes != 792) { return; }
            starved = true; held = budget - metrics.HashMemoryBytes - 45000;
            metrics.ReserveHashMemory(held, budget);
        };
        SqlPlanExecutor.ObserveHashSpill = (phase, value) =>
        {
            if (phase == "join-partitioned" && ++partitioned == 1)
            {
                active!.ReleaseHashMemory(held); held = budget - active.HashMemoryBytes - 300000;
                active.ReserveHashMemory(held, budget);
            }
            if (phase == "join-handoff" && starved) { parent = true; }
            if (phase == "join-sorter-minimum" && parent) { yielded = true; }
            if (phase == "join-ordered" && held != 0) { active!.ReleaseHashMemory(held); held = 0; }
        };
        try
        {
            // Act
            var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            try
            {
                var rows = await Rows(actual); rows.ShouldBe(await Rows(expected));
                b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
                b.LastStatementMetrics.RecordsExamined.ShouldBe(a.LastStatementMetrics.RecordsExamined);
                _output.WriteLine($"FIX3_NESTED|digest={Fix2Fingerprint(rows)}|pairs={b.LastStatementMetrics.JoinCandidatePairs}|examined={b.LastStatementMetrics.RecordsExamined}");
            }
            finally { await actual.DisposeAsync(); }
            // Assert
            starved.ShouldBeTrue(); parent.ShouldBeTrue(); yielded.ShouldBeTrue();
        }
        finally
        {
            active?.ReleaseHashMemory(held); SqlStatementMetrics.AfterMemoryReservation = null; SqlPlanExecutor.ObserveHashSpill = null;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Zero-candidate flat hash steps keep their policy and NULL actuals (#1789)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_JoinFix3UnbuiltPolicy_ShouldKeepNullActuals(bool outerRow, bool zeroLimit)
    {
        // Arrange
        await using var engine = await CreateFix2("fix3-unbuilt", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE jl(id INT)"); await session.ExecuteAsync("CREATE TABLE jr(id INT)");
        await session.ExecuteAsync("INSERT INTO jr VALUES(1)");
        if (outerRow) { await session.ExecuteAsync("INSERT INTO jl VALUES(2)"); }
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN ANALYZE SELECT jl.id,jr.id FROM jl LEFT JOIN jr ON jl.id=jr.id" + (zeroLimit ? " LIMIT 0" : ""));
        var rows = await Rows(result);
        string text = string.Join('\n', rows.Select(row => row.ToString()));
        _output.WriteLine("FIX3_UNBUILT|" + text);
        // Assert
        text.ShouldContain("hash_budget=spill_on_refusal; hash_status=fitting", Case.Sensitive);
        rows.Single(row => Equals(row.Cells[2], "JoinStep")).Cells[7].ShouldBeNull();
    }
#endif


    [Fact(DisplayName = "Cohesion Test [Sql] - A staged FULL reader can reclaim another operator's optional arena (#1789)")]
    public async Task ExecuteAsync_JoinFix3OptionalArena_ShouldYieldToStagedReader()
    {
        // Arrange
        await using var engine = await CreateFix2("fix3-optional", 262144);
        await using var reference = await CreateFix2("fix3-optional-reference", long.MaxValue);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await session.ExecuteAsync("CREATE TABLE jl(id INT,payload TEXT)");
            await session.ExecuteAsync("INSERT INTO jl VALUES" + string.Join(',', Enumerable.Range(0, 200).Select(i => $"({i},'{new string('x', 2500)}')")));
            await session.ExecuteAsync("CREATE TABLE jr(id INT," + string.Join(',', Enumerable.Range(0, 150).Select(i => $"b{i} CHAR(30)")) + ")");
            string cells = string.Join(',', Enumerable.Repeat("'" + new string('y', 30) + "'", 150));
            await session.ExecuteAsync("INSERT INTO jr VALUES" + string.Join(',', Enumerable.Range(0, 50).Select(i => $"({i},{cells})")));
        }
        const string sql = "SELECT jl.id,jr.id FROM jl FULL JOIN jr ON jl.id=jr.id";
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
        // Act
        var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
        try
        {
            var rows = await Rows(actual); rows.ShouldBe(await Rows(expected));
            b.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics!.JoinCandidatePairs);
            b.LastStatementMetrics.RecordsExamined.ShouldBe(a.LastStatementMetrics.RecordsExamined);
            _output.WriteLine($"FIX3_OPTIONAL|digest={Fix2Fingerprint(rows)}|rows={rows.Length}|pairs={b.LastStatementMetrics.JoinCandidatePairs}|examined={b.LastStatementMetrics.RecordsExamined}");
        }
        finally { await actual.DisposeAsync(); }
    }


#if !L3B_BASE
    [Fact(DisplayName = "Cohesion Test [Sql] - A nested FULL child's unmatched minimum reclaims the parent's optional arena (#1789)")]
    public async Task Unmatched_JoinFix3NestedArena_ShouldYieldStatementExtra()
    {
        // Arrange: the two live tables are the parent build and child of A FULL (B FULL C).
        const long budget = 262144;
        await using var engine = await CreateFix2("fix3-nested", budget, quota: 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics(); var keys = new SqlHashJoinPath([0], [0], [Collation.Binary]);
        using var child = new SqlHashJoinTable(keys, metrics, budget, trackUnmatched: true);
        using var parent = new SqlHashJoinTable(keys, metrics, budget, trackUnmatched: true);
        long held = 0; bool starved = false, childSpilled = false, parentSpilled = false;
        try
        {
            var random = new Random(178910);
            for (int i = 0; i < 400; i++)
            {
                var bytes = new byte[384]; random.NextBytes(bytes); object?[] row = [1L, Convert.ToBase64String(bytes)];
                try { child.Add(row, CancellationToken.None); }
                catch (SqlExecutionException error) when (!childSpilled && error.Diagnostics[0].Code == "CS05067")
                { child.Spill(session, row, CancellationToken.None, error); childSpilled = true; }
            }
            SqlStatementMetrics.AfterMemoryReservation = (owner, bytes) =>
            {
                if (!ReferenceEquals(owner, metrics) || bytes != 792 || starved) { return; }
                starved = true; held = budget - metrics.HashMemoryBytes - 21000;
                metrics.ReserveHashMemory(held, budget);
            };
            child.Probe([2L], CancellationToken.None).Count().ShouldBe(0);
            SqlStatementMetrics.AfterMemoryReservation = null;
            metrics.ReleaseHashMemory(held); held = 0;
            for (int i = 0; i < 20; i++)
            {
                object?[] row = Enumerable.Repeat<object?>(true, 700).ToArray(); row[0] = 1L;
                try { parent.Add(row, CancellationToken.None); }
                catch (SqlExecutionException error) when (!parentSpilled && error.Diagnostics[0].Code == "CS05067")
                { parent.Spill(session, row, CancellationToken.None, error); parentSpilled = true; }
            }
            // Act / Assert: the child now needs M while the parent's sorter is still open.
            child.Unmatched(CancellationToken.None).Count().ShouldBe(400);
            parent.Probe([1L], CancellationToken.None).Count().ShouldBe(20);
            starved.ShouldBeTrue(); childSpilled.ShouldBeTrue(); parentSpilled.ShouldBeTrue();
        }
        finally
        {
            SqlStatementMetrics.AfterMemoryReservation = null; metrics.ReleaseHashMemory(held);
            parent.Dispose(); child.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose();
        }
        metrics.HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CRC-valid directory range tampering refuses before probes (#1789)")]
    public async Task ExecuteAsync_JoinFix3DirectoryDamage_ShouldLocateAndRelease()
    {
        // Arrange
        await using var engine = await CreateFix2("fix3-directory-damage", 262144, quota: 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedFix2(session, 400, 512);
        bool injected = false;
        BeforeRead(engine.TempSpace!, () =>
        {
            if (injected) { return; }
            var directory = StatementRuns(engine.TempSpace!).FirstOrDefault(run => run.IsSealed && run.MaximumRecordLength == 48);
            if (directory is null) { return; }
            injected = true; // Prevent re-entry while finding a hash absent from this build.
            var hashes = new HashSet<uint>();
            var index = StatementRuns(engine.TempSpace!).Single(run => run.IsSealed && run.RecordCount == 400 && run.MaximumRecordLength != 48);
            using (var reader = index.OpenReader(8192))
            {
                byte[] frame = new byte[index.MaximumRecordLength];
                while (reader.TryRead(frame, out _)) { hashes.Add(BinaryPrimitives.ReadUInt32BigEndian(frame)); }
            }
            uint absent = 0; while (hashes.Contains(absent)) { absent++; }
            byte[] page = new byte[8192]; RandomAccess.Read(Handle(directory), page, 0);
            // Each entry remains structurally valid, but no real probe hash falls in it.
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(36), absent);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(40), absent);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28), 0);
            uint crc = uint.MaxValue;
            foreach (byte value in page)
            { crc ^= value; for (int bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82f63b78u); } }
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28), ~crc);
            RandomAccess.Write(Handle(directory), page, 0);
        });
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync("SELECT jl.id,jr.id FROM jl LEFT JOIN jr ON jl.id=jr.id"));
            // Assert
            injected.ShouldBeTrue(); error.Diagnostics[0].Code.ShouldBe("CK05004");
            error.Diagnostics[0].Location.ShouldNotBeNull();
            Statements(engine.TempSpace!).ShouldBeEmpty();
        }
        finally { BeforeRead(engine.TempSpace!, null); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Wider rows and large directories pin real product reservations (#1789)")]
    [InlineData("shrink")]
    [InlineData("directory")]
    public async Task Add_JoinFix3Reservations_ShouldPinShrinkAndGrowth(string shape)
    {
        // Arrange
        long budget = shape == "shrink" ? 1048576 : 262144;
        await using var engine = await CreateFix2("fix3-reservations", budget, quota: 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics();
        using var table = new SqlHashJoinTable(new([0], [0], [Collation.Binary]), metrics, budget);
        var reservations = new List<long>(); bool minimum = false, spilled = false;
        SqlStatementMetrics.AfterMemoryReservation = (owner, bytes) => { if (ReferenceEquals(metrics, owner)) { reservations.Add(bytes); } };
        SqlPlanExecutor.ObserveHashSpill = (phase, value) => { minimum |= phase == "join-sorter-minimum" && value == 81920; };
        long held = 0;
        try
        {
            var random = new Random(178909);
            for (int i = 0; i < (shape == "shrink" ? 1100 : 5000); i++)
            {
                var bytes = new byte[384]; random.NextBytes(bytes);
                object?[] row = [1L, shape == "shrink" ? new string('x', 512) : Convert.ToBase64String(bytes)];
                try { table.Add(row, CancellationToken.None); }
                catch (SqlExecutionException error) when (!spilled && error.Diagnostics[0].Code == "CS05067")
                { table.Spill(session, row, CancellationToken.None, error); spilled = true; reservations.Clear(); }
            }
            // Act
            if (shape == "shrink")
            {
                held = budget - metrics.HashMemoryBytes - 20000; metrics.ReserveHashMemory(held, budget);
                table.Add([1L, new string('y', 15000)], CancellationToken.None);
                metrics.ReleaseHashMemory(held); held = 0;
            }
            table.Probe([1L], CancellationToken.None).Count().ShouldBe(shape == "shrink" ? 1101 : 5000);
            // Assert
            _output.WriteLine($"FIX3_RESERVATIONS|{shape}|" + string.Join(',', reservations));
            if (shape == "shrink") { minimum.ShouldBeTrue(); reservations.Count(value => value == 81920).ShouldBe(1); }
            else
            {
                foreach (long bytes in new long[] { 792, 1560, 3096, 6168, 12312, 24600 })
                { reservations.Count(value => value == bytes).ShouldBe(1); }
            }
        }
        finally
        {
            SqlStatementMetrics.AfterMemoryReservation = null; SqlPlanExecutor.ObserveHashSpill = null;
            metrics.ReleaseHashMemory(held); table.Dispose(); SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose();
        }
        metrics.HashMemoryBytes.ShouldBe(0);
    }
#endif
}
