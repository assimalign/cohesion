using System;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Borrowed values and context of one function call.</summary>
[global::System.Diagnostics.DebuggerDisplay("DocumentArguments, Count = {Count}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentArguments.DebugView))]
public readonly ref struct DocumentArguments
{
    private readonly ReadOnlySpan<DocumentValue> _values;
    private readonly ReadOnlySpan<object?> _objects;
    private readonly DocumentFunctionContext _context;

    /// <summary>Initializes direct-call arguments.</summary><param name="values">The borrowed values.</param>
    public DocumentArguments(ReadOnlySpan<DocumentValue> values) { _values = values; }
    /// <summary>Initializes arguments with a context.</summary><param name="values">The borrowed values.</param><param name="context">The context.</param>
    public DocumentArguments(ReadOnlySpan<DocumentValue> values, scoped in DocumentFunctionContext context) { _values = values; _context = context; }
    internal DocumentArguments(ReadOnlySpan<object?> values, scoped in DocumentFunctionContext context) { _objects = values; _context = context; }
    /// <summary>Gets the argument count.</summary>
    public int Count => _objects.IsEmpty ? _values.Length : _objects.Length;
    /// <summary>Gets an argument.</summary><param name="index">The position.</param><returns>The borrowed value.</returns><exception cref="IndexOutOfRangeException">The index is outside this call.</exception>
    public DocumentValue this[int index] => _objects.IsEmpty ? _values[index] : new(_objects[index]);
    /// <summary>Gets the call context.</summary>
    public DocumentFunctionContext Context => _context;
    /// <summary>Checks for NULL.</summary><param name="index">The position.</param><returns>Whether it is NULL.</returns>
    public bool IsNull(int index) => this[index].IsNull;
    /// <summary>Checks for MISSING.</summary><param name="index">The position.</param><returns>Whether it is MISSING.</returns>
    public bool IsMissing(int index) => this[index].IsMissing;
    /// <summary>Reads a Boolean.</summary><param name="index">The position.</param><returns>The Boolean.</returns>
    public bool GetBoolean(int index) => this[index].AsBoolean();
    /// <summary>Reads an integer.</summary><param name="index">The position.</param><returns>The integer.</returns>
    public long GetInt64(int index) => this[index].AsInt64();
    /// <summary>Reads a number.</summary><param name="index">The position.</param><returns>The decimal.</returns>
    public decimal GetDecimal(int index) => this[index].AsDecimal();
    /// <summary>Reads a string.</summary><param name="index">The position.</param><returns>The string.</returns>
    public string GetString(int index) => this[index].AsString();
    /// <summary>Reads a borrowed JSON array or object.</summary><param name="index">The position.</param><returns>The JSON.</returns>
    public JsonElement GetJson(int index) => this[index].AsJson();

    private sealed class DebugView
    {
        private readonly int _count;
        public DebugView(DocumentArguments value) { _count = value.Count; }
        public int Count => _count;
    }
}
