using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A read-only dictionary that yields one entry under a null key, which no framework dictionary allows, to
/// prove the encoder refuses a null key from any dictionary an application supplies.
/// </summary>
internal sealed class TestNullKeyDictionary : IReadOnlyDictionary<string, object?>
{
    public int Count => 1;

    public IEnumerable<string> Keys => [null!];

    public IEnumerable<object?> Values => [1];

    public object? this[string key] => 1;

    public bool ContainsKey(string key)
    {
        return key is null;
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value)
    {
        value = 1;
        return key is null;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return new KeyValuePair<string, object?>(null!, 1);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
