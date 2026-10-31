using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The deliberately closed SQL/JSON accessor grammar. Parsed before row access.</summary>
[System.Diagnostics.DebuggerDisplay("SQL/JSON path")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlJsonPath
{
    private readonly Step[] _steps;
    private readonly bool _strict;

    private SqlJsonPath(Step[] steps, bool strict) { _steps = steps; _strict = strict; }

    internal static SqlJsonPath Parse(string text)
    {
        if (text.Length > 65536) { throw new SqlExecutionException("CS05094", ErrorMessages.JsonPathBudget); }
        int position = 0;
        Space();
        bool strict = Word("strict");
        if (!strict) { Word("lax"); }
        Space();
        Require('$');
        var steps = new List<Step>();
        while (true)
        {
            Space();
            if (position == text.Length) { return new SqlJsonPath(steps.ToArray(), strict); }
            if (steps.Count == 256) { throw new SqlExecutionException("CS05094", ErrorMessages.JsonPathBudget); }
            if (Take('.'))
            {
                Space();
                if (Take('*')) { steps.Add(new Step(null, true, null)); }
                else if (position < text.Length && text[position] == '"')
                {
                    int first = position++;
                    bool closed = false;
                    while (position < text.Length)
                    {
                        char c = text[position++];
                        if (c == '\\' && position < text.Length) { position++; }
                        else if (c == '"') { closed = true; break; }
                    }
                    if (!closed) { throw Invalid(); }
                    try
                    {
                        using var document = SqlJsonAccess.ReadExtraction(text[first..position]);
                        steps.Add(new Step(document.RootElement.GetString(), false, null));
                    }
                    catch (DatabaseException) { throw Invalid(); }
                }
                else
                {
                    int first = position;
                    if (position == text.Length || !(char.IsLetter(text[position]) || text[position] == '_')) { throw Invalid(); }
                    while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_')) { position++; }
                    steps.Add(new Step(text[first..position], false, null));
                }
            }
            else if (Take('['))
            {
                Space();
                if (Take('*')) { Space(); Require(']'); steps.Add(new Step(null, false, [])); }
                else
                {
                    var ranges = new List<Subscript>();
                    do
                    {
                        var from = Index(); Space();
                        var to = Word("to") ? Index() : from;
                        ranges.Add(new Subscript(from, to)); Space();
                    } while (Take(','));
                    Require(']'); steps.Add(new Step(null, false, ranges.ToArray()));
                }
            }
            else { throw Invalid(); }
        }

        IndexValue Index()
        {
            Space(); bool last = Word("last"); Space();
            if (last && !Take('-')) { return new IndexValue(true, 0); }
            Space(); int first = position;
            if (!last && position < text.Length && text[position] == '-') { position++; }
            while (position < text.Length && char.IsAsciiDigit(text[position])) { position++; }
            if (!int.TryParse(text.AsSpan(first, position - first), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number)) { throw Invalid(); }
            return new IndexValue(last, last ? -(long)number : number);
        }
        bool Word(string word)
        {
            Space();
            if (!text.AsSpan(position).StartsWith(word, StringComparison.OrdinalIgnoreCase) || position + word.Length < text.Length &&
                (char.IsLetterOrDigit(text[position + word.Length]) || text[position + word.Length] == '_')) { return false; }
            position += word.Length; Space(); return true;
        }
        void Space() { while (position < text.Length && text[position] is ' ' or '\t' or '\r' or '\n' or '\f') { position++; } }
        bool Take(char character) { if (position == text.Length || text[position] != character) { return false; } position++; return true; }
        void Require(char character) { if (!Take(character)) { throw Invalid(); } }
    }

    internal List<JsonElement> Evaluate(JsonElement root, SqlJsonPathExecution work, bool exists)
    {
        var result = new List<JsonElement>();
        Visit(root, 0, true);
        return result;

        bool Visit(JsonElement item, int stepIndex, bool unwrap)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            work.Visit();
            if (stepIndex == _steps.Length)
            {
                if (!exists || result.Count == 0) { work.Reserve(32); result.Add(item); }
                return exists && !_strict;
            }
            var step = _steps[stepIndex];
            if (step.Indices is { } indices)
            {
                bool array = item.ValueKind == JsonValueKind.Array;
                if (!array && _strict) { throw Structural(); }
                int size = array ? item.GetArrayLength() : 1;
                if (indices.Length == 0)
                {
                    if (!array) { return Visit(item, stepIndex + 1, true); }
                    foreach (var child in item.EnumerateArray())
                    { if (Visit(child, stepIndex + 1, true)) { return true; } }
                }
                else
                {
                    foreach (var range in indices)
                    {
                        long from = range.From.Resolve(size), to = range.To.Resolve(size);
                        if (_strict && (from < 0 || from > to || to >= size)) { throw Structural(); }
                        from = Math.Max(0, from); to = Math.Min(size - 1, to);
                        // Enumerate once per range: JsonElement's indexed lookup scans from
                        // the start. A wildcard or range must stay O(input + results).
                        if (array)
                        {
                            long index = 0;
                            foreach (var child in item.EnumerateArray())
                            {
                                if (index > to) { break; }
                                work.Visit();
                                if (index++ >= from && Visit(child, stepIndex + 1, true)) { return true; }
                            }
                        }
                        else if (from == 0 && to >= 0 && Visit(item, stepIndex + 1, true)) { return true; }
                    }
                }
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (step.Wildcard)
                {
                    // SQL/JSON operates on jsonb: duplicates have already collapsed and
                    // object iteration uses jsonb's length/UTF-8 key order.
                    foreach (var property in item.EnumerateObject())
                    { if (Visit(property.Value, stepIndex + 1, true)) { return true; } }
                }
                else
                {
                    // Charge every inspected member, including a failed lookup.
                    JsonElement? value = null;
                    foreach (var property in item.EnumerateObject())
                    { work.Visit(); if (property.NameEquals(step.Name!)) { value = property.Value; } }
                    if (value is { } found) { return Visit(found, stepIndex + 1, true); }
                    if (_strict) { throw Structural(); }
                }
            }
            else if (!_strict && unwrap && item.ValueKind == JsonValueKind.Array)
            {
                // Exactly one unwrapping level per member accessor (not recursive).
                foreach (var child in item.EnumerateArray())
                { if (Visit(child, stepIndex, false)) { return true; } }
            }
            else if (_strict) { throw Structural(); }
            return false;
        }
    }

    internal static SqlExecutionException Invalid() => new("CS02081", ErrorMessages.JsonPathInvalid);
    private static SqlExecutionException Structural() => new("CS02082", ErrorMessages.JsonPathStructural);
    private sealed record Step(string? Name, bool Wildcard, Subscript[]? Indices);
    private readonly record struct Subscript(IndexValue From, IndexValue To);
    private readonly record struct IndexValue(bool Last, long Offset)
    { internal long Resolve(int size) => (Last ? size - 1L : 0) + Offset; }
    private sealed class DebugView { public DebugView(SqlJsonPath value) { _ = value; } }
}
