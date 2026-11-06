using System;
using System.IO;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.KeyValuePair.Catalog;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.FormatProbeFixture;

internal static class Program
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TempSpace")]
    private static extern StorageTempSpace? TempSpace(SqlDatabaseEngine engine);

    private static async Task TempLeaseProbeAsync(string root)
    {
        string temp = Path.Combine(root, ".cohesion-temp"); CreateTempDirectory(temp);
        string orphan = Path.Combine(temp, "cohesion-00000000000000000000000000000001.ctmp"); File.WriteAllBytes(orphan, [1]);
        using var events = new TempWarnings();
        await using (var seed = SqlDatabaseEngine.Create("seed", new() { RootPath = root, CheckpointJournalSize = 65536 }))
        { await seed.CreateDatabaseAsync("online", CancellationToken.None); }
        await using var engine = SqlDatabaseEngine.Create("reopen", new() { RootPath = root, CheckpointJournalSize = 65536 });
        await engine.OpenDatabaseAsync("online", CancellationToken.None);
        using var statement = TempSpace(engine)!.CreateStatement(8192);
        try
        {
            using var run = statement.CreateRun(); run.Append([1]); run.Seal();
            if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("disabled locking admitted a run"); }
            Console.WriteLine("TEMP_LEASE_OK online");
        }
        catch (StorageTempException error) when (!OperatingSystem.IsWindows())
        {
            if (error.Code != "CK05005" || events.Warnings < 2 || !File.Exists(orphan))
            { throw new InvalidOperationException("missing coded refusal, warning or orphan"); }
            Console.WriteLine("TEMP_LEASE_REFUSED CK05005 online warnings=" + events.Warnings);
            Console.WriteLine(error.InnerException!.Message);
        }
    }
    private sealed class TempWarnings : EventListener
    {
        public int Warnings;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Assimalign.Cohesion.Database.Storage") { EnableEvents(source, EventLevel.Warning); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        { if (data.EventId == 23) { Interlocked.Increment(ref Warnings); } }
    }

    private static void CreateTempDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    private static async Task Main(string[] args)
    {
        if (args[0] == "temp-lease-probe") { await TempLeaseProbeAsync(args[1]); return; }
        // This process deliberately exits with live physical brackets. Never dispose them before Exit.
        string model = args[0];
        string root = args[1];
        bool clean = args[2].StartsWith("clean-", StringComparison.Ordinal);
        string mode = clean ? args[2][6..] : args[2];
        bool legacy = mode == "legacy";
        string directory = Path.Combine(root, "probe");
        switch (model)
        {
            case "Graph":
                await using (var engine = GraphDatabaseEngine.Create("seed", new() { RootPath = root }))
                { var database = await engine.CreateDatabaseAsync("probe", CancellationToken.None);
                  await using var session = await database.CreateSessionAsync(CancellationToken.None);
                  await session.ExecuteAsync("CREATE (:Seed {value:7})", cancellationToken: CancellationToken.None);
                  await GraphSchema.Open(database, session).CreateIndexAsync("Seed", "by_value", "value", cancellationToken: CancellationToken.None); }
                break;
            case "Documents":
                await using (var engine = DocumentDatabaseEngine.Create("seed", new() { RootPath = root }))
                { var database = await engine.CreateDatabaseAsync("probe", CancellationToken.None);
                  await using var session = await database.CreateSessionAsync(CancellationToken.None);
                  await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
                  await session.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: CancellationToken.None); }
                break;
            case "Blob":
                await using (var engine = BlobDatabaseEngine.Create("seed", new() { RootPath = root }))
                { await engine.CreateDatabaseAsync("probe", CancellationToken.None); }
                break;
            case "Sql":
                await using (var engine = SqlDatabaseEngine.Create("seed", new() { RootPath = root }))
                { var database = await engine.CreateDatabaseAsync("probe", CancellationToken.None);
                  await using var session = await database.CreateSessionAsync(CancellationToken.None);
                  await session.ExecuteAsync("CREATE TABLE items(id INT PRIMARY KEY, value INT)", cancellationToken: CancellationToken.None);
                  await session.ExecuteAsync("INSERT INTO items VALUES (1, 7)", cancellationToken: CancellationToken.None); }
                break;
            case "KeyValuePair":
                await using (var engine = KeyValueDatabaseEngine.Create("seed", new() { RootPath = root }))
                { await engine.CreateDatabaseAsync("probe", CancellationToken.None); }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(model));
        }

        StorageStream Open(string set, string extension) => StorageStream.FromFile(Path.Combine(directory, set + extension), FileMode.Open, FileShare.Read);
        // A sentinel page is checkpointed before any crash workload. Its next first
        // touch must journal an uncommitted full image, independently of later allocations.
        foreach (string set in model is "Sql" or "KeyValuePair" ? new[] { "catalog", model == "Sql" ? "sql" : "keyvalue" }
            : new[] { model == "Documents" ? "document" : model.ToLowerInvariant() })
        {
            using var prepared = OpenSet(set);
            var transaction = prepared.BeginTransaction();
            using (var page = prepared.AllocatePageForWrite(transaction, PageType.Data, ulong.MaxValue))
            {
                new SlottedPage(page.Page).Initialize();
                page.Page.AsSpan()[512] = 0x43;
                page.MarkDirty();
                File.WriteAllText(Path.Combine(directory, set + ".page"), ((long)page.Id).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            transaction.Commit();
            prepared.Checkpoint();
        }

        Assimalign.Cohesion.Database.Storage.Storage OpenSet(string set) => model switch
        {
            "Graph" => GraphStorage.Open(Open(set, ".dat"), Open(set, ".log"), Open(set, ".bak"), checkpointOnOpen: false),
            "Documents" => DocumentStorage.Open(Open(set, ".dat"), Open(set, ".log"), Open(set, ".bak"), checkpointOnOpen: false),
            "Blob" => BlobStorage.Open(Open(set, ".dat"), Open(set, ".log"), Open(set, ".bak"), checkpointOnOpen: false),
            "Sql" => SqlStorage.Open(Open(set, ".dat"), Open(set, ".log"), Open(set, ".bak"), checkpointOnOpen: false),
            "KeyValuePair" => KeyValueStorage.Open(Open(set, ".dat"), Open(set, ".log"), Open(set, ".bak"), checkpointOnOpen: false),
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };

        Assimalign.Cohesion.Database.Storage.Storage storage;
        string dataSet;
        if (model == "Sql")
        {
            var catalog = SqlStorage.Open(Open("catalog", ".dat"), Open("catalog", ".log"), Open("catalog", ".bak"), checkpointOnOpen: false);
            if (legacy || mode is "format34-owner" or "format35" or "format36")
            { await SqlCatalog.Open(catalog).SetRecordSpaceFormatVersionAsync(mode == "format34-owner" ? 34 : mode == "format35" ? 35 : mode == "format36" ? 36 : 33, CancellationToken.None); }
            LeaveUncommitted(catalog, "catalog");
            dataSet = "sql";
            storage = SqlStorage.Open(Open(dataSet, ".dat"), Open(dataSet, ".log"), Open(dataSet, ".bak"), checkpointOnOpen: false);
            if (mode == "format34-owner")
            {
                var registration = SqlCatalog.Open(catalog).GetIndexRegistrations()[0];
                using var bracket = storage.BeginTransaction();
                using (var page = storage.OpenPageForWrite(bracket, (PageId)registration.RootPageId))
                { var rootPage = page.Page; rootPage.OwnerId = ulong.MaxValue - 1; page.MarkDirty(); }
                bracket.Commit();
            }
        }
        else if (model == "KeyValuePair")
        {
            var catalog = KeyValueStorage.Open(Open("catalog", ".dat"), Open("catalog", ".log"), Open("catalog", ".bak"), checkpointOnOpen: false);
            if (legacy) { await KeyValueCatalog.Open(catalog).SetEntrySpaceFormatVersionAsync(1, CancellationToken.None); }
            LeaveUncommitted(catalog, "catalog");
            dataSet = "keyvalue";
            storage = KeyValueStorage.Open(Open(dataSet, ".dat"), Open(dataSet, ".log"), Open(dataSet, ".bak"), checkpointOnOpen: false);
        }
        else
        {
            dataSet = model == "Documents" ? "document" : model.ToLowerInvariant();
            byte current = model == "Graph" ? (byte)3 : (byte)1;
            byte[] record = Metadata(legacy ? (model == "Graph" ? (byte)2 : (byte)0) : current);
            if (model == "Graph")
            {
                var typed = GraphStorage.Open(Open(dataSet, ".dat"), Open(dataSet, ".log"), Open(dataSet, ".bak"), checkpointOnOpen: false);
                var bracket = typed.BeginTransaction(); typed.InsertEntry(bracket, record); bracket.Commit(); storage = typed;
            }
            else if (model == "Documents")
            {
                var typed = DocumentStorage.Open(Open(dataSet, ".dat"), Open(dataSet, ".log"), Open(dataSet, ".bak"), checkpointOnOpen: false);
                var bracket = typed.BeginTransaction(); typed.InsertEntry(bracket, record); bracket.Commit(); storage = typed;
            }
            else
            {
                var typed = BlobStorage.Open(Open(dataSet, ".dat"), Open(dataSet, ".log"), Open(dataSet, ".bak"), checkpointOnOpen: false);
                var bracket = typed.BeginTransaction(); typed.InsertEntry(bracket, record); bracket.Commit(); storage = typed;
            }
        }
        if (storage is GraphStorage graph && mode is "entity" or "registration")
        {
            using var records = graph.GetUnitIterator(mode == "entity" ? 2UL : 1UL);
            if (!records.MoveNext()) { throw new InvalidOperationException("Graph seed has no format-gated record."); }
            var unit = records.Current;
            byte[] bytes = unit.Data.ToArray();
            bytes[17] = 4;
            var bracket = graph.BeginTransaction();
            graph.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, bytes);
            bracket.Commit();
        }
        { // Journal each current root too, so a torn format stamp remains recoverable.
            var bracket = storage.BeginTransaction();
            for (long id = 1; id < storage.Data.Length / Page.Size; id++)
            {
                using var read = storage.PageManager.GetPage((PageId)id);
                if (read.Page.Type != PageType.Index) { continue; }
                using var write = storage.OpenPageForWrite(bracket, (PageId)id);
                if (mode is "index" or "index2") { write.Page.AsBodySpan()[2] = mode == "index2" ? (byte)2 : (byte)1; }
                write.MarkDirty();
            }
            bracket.Commit();
        }
        LeaveUncommitted(storage, dataSet);
        Console.WriteLine("SEEDED");
        Environment.Exit(0);

        void LeaveUncommitted(Assimalign.Cohesion.Database.Storage.Storage target, string set)
        {
            if (clean) { target.Checkpoint(); target.Dispose(); return; }
            var committed = target.BeginTransaction();
            using (var page = target.AllocatePageForWrite(committed, PageType.Data, ulong.MaxValue))
            {
                new SlottedPage(page.Page).Initialize();
                page.MarkDirty();
            }
            committed.Commit();
            PageId pageId = (PageId)long.Parse(File.ReadAllText(Path.Combine(directory, set + ".page")), System.Globalization.CultureInfo.InvariantCulture);
            var uncommitted = target.BeginTransaction();
            using (var page = target.OpenPageForWrite(uncommitted, pageId))
            {
                page.Page.AsSpan()[512] = 0x55;
                page.MarkDirty();
            }
            target.PageManager.FlushAll();
            target.Journal.FlushDurable();
            File.WriteAllText(Path.Combine(directory, set + ".page"), ((long)pageId).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        byte[] Metadata(byte version)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            if (model == "Documents" && legacy)
            {
                writer.Write(0UL); writer.Write(0UL); writer.Write((byte)4); writer.Write((byte)2);
                writer.Write(Guid.NewGuid().ToByteArray()); writer.Write(2); writer.Write("ix"u8);
                writer.Write(1); writer.Write(5); writer.Write("value"u8); writer.Write(false); writer.Write(1UL);
                return stream.ToArray();
            }
            writer.Write(0UL); writer.Write(0UL); writer.Write((byte)1); writer.Write(version);
            writer.Write(Guid.NewGuid().ToByteArray());
            writer.Write(9); writer.Write("committed"u8);
            writer.Write((byte)0); writer.Write(-1);
            return stream.ToArray();
        }
    }
}
