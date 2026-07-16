using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// Ordered key scans: ascending byte order, inclusive/exclusive bounds, prefix
/// ranges, and limits — the order-preserving range surface that is the key-value
/// model's reason to sit on the B+Tree.
/// </summary>
public class KeyValueScanTests
{
    private static async Task SeedAsync(IKeyValueDatabase database, IDatabaseSession session, params string[] keys)
    {
        foreach (string key in keys)
        {
            (await database.PutAsync(session, Bytes(key), Bytes("v-" + key), cancellationToken: TestTimeout.Token()))
                .Applied.ShouldBeTrue();
        }
    }

    private static async Task<List<string>> CollectKeysAsync(IKeyValueDatabase database, IDatabaseSession session, KeyValueScanOptions? options = null)
    {
        var keys = new List<string>();

        await foreach (var entry in database.ScanAsync(session, options, TestTimeout.Token()))
        {
            keys.Add(Text(entry.Key));
        }

        return keys;
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scan: Should stream every entry in ascending key order regardless of insert order")]
    public async Task Scan_Unbounded_ShouldReturnAscendingKeyOrder()
    {
        // Arrange: inserted deliberately out of order.
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await SeedAsync(database, session, "delta", "alpha", "charlie", "bravo");

        // Act
        var keys = await CollectKeysAsync(database, session);

        // Assert
        keys.ShouldBe(["alpha", "bravo", "charlie", "delta"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scan: Bounds should be start-inclusive and end-exclusive")]
    public async Task Scan_WithBounds_ShouldHonorInclusivity()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await SeedAsync(database, session, "a", "b", "c", "d");

        // Act
        var keys = await CollectKeysAsync(database, session, new KeyValueScanOptions
        {
            Start = Bytes("b"),
            End = Bytes("d"),
        });

        // Assert: [b, d) — start inclusive, end exclusive.
        keys.ShouldBe(["b", "c"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scan: A prefix should cover exactly the keys carrying it")]
    public async Task Scan_WithPrefix_ShouldReturnPrefixedKeysOnly()
    {
        // Arrange: "user:" ordering neighbors on both sides ("user" sorts before
        // "user:", "userz" after every "user:x").
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await SeedAsync(database, session, "user", "user:1", "user:2", "user:9", "userz", "vendor:1");

        // Act
        var keys = await CollectKeysAsync(database, session, new KeyValueScanOptions { Prefix = Bytes("user:") });

        // Assert
        keys.ShouldBe(["user:1", "user:2", "user:9"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scan: An all-0xFF prefix scans to the end of the key space")]
    public async Task Scan_WithMaxBytePrefix_ShouldScanToEnd()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        byte[] maxKey = [0xFF, 0x01];
        (await database.PutAsync(session, maxKey, Bytes("top"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();
        await SeedAsync(database, session, "ordinary");

        // Act: the prefix 0xFF has no byte successor — the range is unbounded above.
        var entries = new List<byte[]>();
        await foreach (var entry in database.ScanAsync(session, new KeyValueScanOptions { Prefix = new byte[] { 0xFF } }, TestTimeout.Token()))
        {
            entries.Add(entry.Key.ToArray());
        }

        // Assert
        entries.ShouldHaveSingleItem().ShouldBe(maxKey);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scan: A limit should truncate the result in order")]
    public async Task Scan_WithLimit_ShouldTruncateInOrder()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await SeedAsync(database, session, "a", "b", "c", "d");

        // Act
        var keys = await CollectKeysAsync(database, session, new KeyValueScanOptions { Limit = 2 });

        // Assert
        keys.ShouldBe(["a", "b"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scan: Deleted entries should not appear")]
    public async Task Scan_AfterDelete_ShouldOmitDeletedEntries()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await SeedAsync(database, session, "a", "b", "c");
        await database.TryDeleteAsync(session, Bytes("b"), cancellationToken: TestTimeout.Token());

        // Act
        var keys = await CollectKeysAsync(database, session);

        // Assert
        keys.ShouldBe(["a", "c"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scan: Replacing a value should not duplicate its key in a scan")]
    public async Task Scan_AfterReplace_ShouldShowOneVersionPerKey()
    {
        // Arrange: a replace writes a version chain (tombstone + insert); exactly
        // one version per key may be visible to any snapshot.
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await SeedAsync(database, session, "a", "b");
        await database.PutAsync(session, Bytes("a"), Bytes("replaced"), cancellationToken: TestTimeout.Token());

        // Act
        var entries = new List<(string Key, string Value)>();
        await foreach (var entry in database.ScanAsync(session, null, TestTimeout.Token()))
        {
            entries.Add((Text(entry.Key), Text(entry.Value)));
        }

        // Assert
        entries.ShouldBe([("a", "replaced"), ("b", "v-b")]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Scan: A prefix combined with explicit bounds should be rejected")]
    public void ScanRequest_PrefixWithBounds_ShouldThrow()
    {
        // Arrange
        var options = new KeyValueScanOptions { Prefix = Bytes("p"), Start = Bytes("a") };

        // Act / Assert
        Should.Throw<System.ArgumentException>(() => new KeyValueScanRequest(options));
    }
}
