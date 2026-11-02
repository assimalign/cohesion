using System;
using System.IO;
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
    private static async Task Main(string[] args)
    {
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
            if (legacy) { await SqlCatalog.Open(catalog).SetRecordSpaceFormatVersionAsync(33, CancellationToken.None); }
            LeaveUncommitted(catalog, "catalog");
            dataSet = "sql";
            storage = SqlStorage.Open(Open(dataSet, ".dat"), Open(dataSet, ".log"), Open(dataSet, ".bak"), checkpointOnOpen: false);
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
            byte current = model == "Graph" ? (byte)2 : (byte)1;
            byte[] record = Metadata(legacy ? (model == "Graph" ? (byte)1 : (byte)0) : current);
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
                if (mode == "index") { write.Page.AsBodySpan()[2] = 1; }
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
