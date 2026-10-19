using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Documents.Tests;

internal sealed class CancellingStringParameters : IReadOnlyDictionary<string, object?>
{
    private readonly CancellationTokenSource _cancellation;
    private readonly string _value;

    internal CancellingStringParameters(CancellationTokenSource cancellation, string value)
    {
        _cancellation = cancellation;
        _value = value;
    }

    internal int ReadCount { get; private set; }
    public int Count => 1;
    public IEnumerable<string> Keys => ["p"];
    public IEnumerable<object?> Values => [_value];
    public object? this[string key] => _value;
    public bool ContainsKey(string key) => key == "p";

    public bool TryGetValue(string key, out object? value)
    {
        ReadCount++;
        _cancellation.Cancel();
        value = _value;
        return key == "p";
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        => new Dictionary<string, object?> { ["p"] = _value }.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
