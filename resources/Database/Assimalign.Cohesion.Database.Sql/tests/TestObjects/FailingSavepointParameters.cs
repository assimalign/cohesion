using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

// A public input seam: application parameter sources may read values from an I/O provider.
internal sealed class FailingSavepointParameters : IReadOnlyDictionary<string, object?>
{
    private readonly IOException _failure;

    internal FailingSavepointParameters(IOException failure) { _failure = failure; }
    internal int Reads { get; private set; }
    public int Count => 1;
    public IEnumerable<string> Keys => ["value"];
    public IEnumerable<object?> Values => throw _failure;
    public object? this[string key] => throw _failure;
    public bool ContainsKey(string key) => key == "value";
    public bool TryGetValue(string key, out object? value) { value = null; throw _failure; }
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() { Reads++; throw _failure; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
