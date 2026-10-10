using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// Documents (OQL). Embedded only: there is no Documents wire server and Documents.Client/src is
/// empty. OQL has no data-mutation syntax, so documents are written through
/// <see cref="DocumentCollection"/> (the collection tools on the page).
/// </summary>
internal sealed class DocumentWorkspace : LanguageWorkspace
{
    private DocumentDatabaseSession? _session;

    public DocumentWorkspace(StudioEngines engines)
        : base(StudioModel.Documents, ConnectionMode.Embedded, engines, wireEndPoint: null)
    {
    }

    public override string LanguageName => "OQL";

    protected override string ClientName => "(none: Documents has no wire client)";

    public override IReadOnlyList<SampleScript> Samples => DocumentSamples.All;

    public override DocumentDatabaseEngine Engine => Engines.Documents;

    public override DocumentDatabaseSession? Session => _session;

    // The session runs its own collection operations (option B, concrete-types plan §6.6).
    private DocumentDatabaseSession DocumentSession => _session ?? throw new InvalidOperationException("Select a database first.");

    protected override async Task OpenSessionAsync(string database, CancellationToken cancellationToken)
    {
        DocumentDatabase opened = await Engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        _session = await opened.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override DatabaseSession? DetachSession()
    {
        DocumentDatabaseSession? session = _session;
        _session = null;
        return session;
    }

    protected override QueryStatement ParseLocally(string statement) => new OqlQueryParser().Parse(statement);

    protected override Task OpenWireAsync(string database, EndPoint endPoint, CancellationToken cancellationToken)
        => throw new NotSupportedException("Documents has no wire server or client.");

    protected override ValueTask CloseWireAsync() => ValueTask.CompletedTask;

    protected override async Task ExecuteStatementAsync(StatementOutcome outcome, ExecuteOptions options, CancellationToken cancellationToken)
    {
        QueryResult result = await RequireSession().ExecuteAsync(outcome.Statement, null, cancellationToken).ConfigureAwait(false);
        await FillFromQueryResultAsync(outcome, result, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<List<CatalogLine>> BuildCatalogAsync(CancellationToken cancellationToken)
    {
        var lines = new List<CatalogLine>();
        var collections = new List<string>();
        await foreach (DocumentCollection collection in DocumentSession.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
        {
            collections.Add(collection.Name);
        }

        TabularResult ownership = await QueryTableAsync(
            "SELECT COLLECTION_NAME, OBJECT_TYPE, OWNER, OWNING_SCHEMA FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY COLLECTION_NAME;", cancellationToken).ConfigureAwait(false);
        TabularResult indexes = await QueryTableAsync(
            "SELECT COLLECTION_NAME, INDEX_NAME, PATH, IS_UNIQUE FROM COHESION_SCHEMA.INDEXES ORDER BY COLLECTION_NAME, INDEX_NAME;", cancellationToken).ConfigureAwait(false);

        lines.Add(new CatalogLine($"Collections ({collections.Count})", IsHeader: true));
        foreach (string name in collections.Order(StringComparer.Ordinal))
        {
            lines.Add(new CatalogLine(name, 1, $"SELECT * FROM {Quote(name)};"));
            foreach (string[] owner in ownership.Rows.Where(row => ownership.Get(row, "COLLECTION_NAME") == name))
            {
                string schema = ownership.Get(owner, "OWNING_SCHEMA");
                lines.Add(new CatalogLine($"owner: {ownership.Get(owner, "OWNER")}{(schema is ("" or ValueFormatter.NullText) ? string.Empty : $" (schema {schema})")}", 2));
            }

            foreach (string[] index in indexes.Rows.Where(row => indexes.Get(row, "COLLECTION_NAME") == name))
            {
                lines.Add(new CatalogLine(
                    $"index {indexes.Get(index, "INDEX_NAME")} ({indexes.Get(index, "PATH")}){(indexes.Get(index, "IS_UNIQUE") == "true" ? " UNIQUE" : string.Empty)}",
                    2,
                    $"DROP INDEX {indexes.Get(index, "INDEX_NAME")} ON {Quote(name)};"));
            }
        }

        lines.Add(new CatalogLine("System collections", IsHeader: true));
        lines.Add(new CatalogLine("COHESION_SCHEMA.INDEXES", 1, "SELECT * FROM COHESION_SCHEMA.INDEXES;"));
        lines.Add(new CatalogLine("COHESION_SCHEMA.OBJECT_OWNERSHIP", 1, "SELECT * FROM COHESION_SCHEMA.OBJECT_OWNERSHIP;"));
        return lines;
    }

    private static string Quote(string name)
        => name.All(c => char.IsLetterOrDigit(c) || c == '_') && name.Length > 0 && !char.IsDigit(name[0]) ? name : $"\"{name}\"";

    public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync<IReadOnlyList<string>>(async token =>
        {
            var names = new List<string>();
            await foreach (DocumentCollection collection in DocumentSession.GetCollectionsAsync(token).ConfigureAwait(false))
            {
                names.Add(collection.Name);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }, cancellationToken);

    public Task CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token => await DocumentSession.CreateCollectionAsync(name, token).ConfigureAwait(false), cancellationToken);

    public Task DropCollectionAsync(string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token => await DocumentSession.DropCollectionAsync(name, token).ConfigureAwait(false), cancellationToken);

    public Task<Document> PutAsync(string collection, string id, string json, ulong? expectedVersion, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            DocumentCollection target = await DocumentSession.GetCollectionAsync(collection, token).ConfigureAwait(false);
            DocumentVersion? expected = expectedVersion is { } version ? new DocumentVersion(version) : null;
            return await target.PutAsync(DocumentSession, id, Encoding.UTF8.GetBytes(json), expected, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Document?> GetAsync(string collection, string id, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            DocumentCollection target = await DocumentSession.GetCollectionAsync(collection, token).ConfigureAwait(false);
            return await target.GetAsync(DocumentSession, id, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<bool> DeleteAsync(string collection, string id, ulong? expectedVersion, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            DocumentCollection target = await DocumentSession.GetCollectionAsync(collection, token).ConfigureAwait(false);
            DocumentVersion? expected = expectedVersion is { } version ? new DocumentVersion(version) : null;
            return await target.DeleteAsync(DocumentSession, id, expected, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Creates <c>items</c> (if missing) and writes a handful of documents the OQL samples query.</summary>
    public Task<int> SeedSampleDataAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            DocumentCollection? items = null;
            await foreach (DocumentCollection collection in DocumentSession.GetCollectionsAsync(token).ConfigureAwait(false))
            {
                if (collection.Name == "items")
                {
                    items = collection;
                }
            }

            items ??= await DocumentSession.CreateCollectionAsync("items", token).ConfigureAwait(false);
            foreach (var (id, json) in DocumentSamples.SeedDocuments)
            {
                await items.PutAsync(DocumentSession, id, Encoding.UTF8.GetBytes(json), null, token).ConfigureAwait(false);
            }

            return DocumentSamples.SeedDocuments.Count;
        }, cancellationToken);
}

internal static class DocumentSamples
{
    public static IReadOnlyList<(string Id, string Json)> SeedDocuments { get; } =
    [
        ("1", """{"name":"anvil","category":"tools","score":3,"price":42.5,"profile":{"city":"Oslo"},"tags":["blue","heavy"]}"""),
        ("2", """{"name":"brush","category":"paint","score":1,"price":4.25,"profile":{"city":"Lima"},"tags":["red"]}"""),
        ("3", """{"name":"chisel","category":"tools","score":5,"price":12,"profile":{"city":"Oslo"},"tags":["blue","sharp"]}"""),
        ("4", """{"name":"dye","category":"paint","score":2,"price":7.75,"profile":{"city":"Kyiv"},"tags":[]}"""),
        ("5", """{"name":"easel","category":"art","profile":{"city":"Quito"},"tags":["wood"]}"""),
    ];

    public static IReadOnlyList<SampleScript> All { get; } =
    [
        new("1. Select everything (seed data first: Collection tools > Seed)", """
            SELECT * FROM items;
            """),
        new("2. Projection, nested paths, filter, order", """
            SELECT d.name, d.profile.city AS city, d.tags[0] AS firstTag, d.score FROM items AS d WHERE d.score >= 2 ORDER BY d.score DESC;
            SELECT name FROM items WHERE score IS NULL;
            """),
        new("3. Group / aggregate", """
            SELECT category, COUNT(*) AS count, COUNT(score) AS scored, SUM(price) AS total, AVG(price) AS average, MIN(price) AS cheapest, MAX(price) AS dearest FROM items GROUP BY category HAVING COUNT(*) >= 1 ORDER BY total DESC;
            """),
        new("4. Index DDL", """
            CREATE INDEX by_score ON items (score);
            SELECT name, score FROM items WHERE score > 1 ORDER BY score;
            SELECT INDEX_NAME, PATH FROM COHESION_SCHEMA.INDEXES WHERE COLLECTION_NAME = 'items';
            DROP INDEX by_score ON items;
            """),
        new("5. Catalog (COHESION_SCHEMA)", """
            SELECT OBJECT_NAME, OBJECT_TYPE, OWNER, OWNING_SCHEMA FROM COHESION_SCHEMA.OBJECT_OWNERSHIP;
            SELECT COLLECTION_NAME, INDEX_NAME, PATH, IS_UNIQUE FROM COHESION_SCHEMA.INDEXES;
            """),
        new("6. Diagnostics demo (expected errors)", """
            SELECT DISTINCT name FROM items;
            """),
    ];
}
