using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class CancelingBindParameters : IReadOnlyDictionary<string, object?>
{
    private readonly CancellationTokenSource _cancellation;

    internal CancelingBindParameters(CancellationTokenSource cancellation) { _cancellation = cancellation; }

    internal int Reads { get; private set; }
    public int Count => 1;
    public IEnumerable<string> Keys => ["gate"];
    public IEnumerable<object?> Values => [1];
    public object? this[string key] => TryGetValue(key, out var value) ? value : null;
    public bool ContainsKey(string key) => key == "gate";
    public bool TryGetValue(string key, out object? value)
    {
        Reads++;
        _cancellation.Cancel();
        value = 1;
        return key == "gate";
    }
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, object?>>)new[] { new KeyValuePair<string, object?>("gate", 1) }).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
