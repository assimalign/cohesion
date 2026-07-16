using System;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// Shared arrangement helpers: an in-memory engine with one open database, and
/// byte conveniences for keys and values.
/// </summary>
internal static class KeyValueTestHarness
{
    public const string DatabaseName = "kv";

    /// <summary>
    /// Creates an in-memory engine with one database named <see cref="DatabaseName"/>.
    /// </summary>
    public static async Task<(KeyValueDatabaseEngine Engine, IKeyValueDatabase Database)> CreateAsync(
        Action<KeyValueDatabaseEngineOptions>? configure = null)
    {
        var options = new KeyValueDatabaseEngineOptions { EngineName = "kv-tests" };
        configure?.Invoke(options);

        var engine = KeyValueDatabaseEngine.Create(options);
        var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync(DatabaseName);

        return (engine, database);
    }

    /// <summary>
    /// UTF-8 bytes for readable test keys and values.
    /// </summary>
    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// Decodes entry bytes back to text for readable assertions.
    /// </summary>
    public static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);
}
