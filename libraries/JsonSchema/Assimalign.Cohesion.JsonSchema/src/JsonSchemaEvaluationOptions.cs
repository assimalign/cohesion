using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// Options for evaluating an instance against a schema.
/// </summary>
[DebuggerDisplay("{OutputFormat}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaEvaluationOptions
{
    private readonly int _maxEvaluationDepth = 512;
    private readonly IReadOnlyList<IJsonSchemaFormat>? _formats;
    private readonly Dictionary<string, IJsonSchemaFormat>? _formatsByName;

    /// <summary>
    /// Gets the options every evaluation uses when none are given: <see cref="JsonSchemaOutputFormat.Flag"/>
    /// output, formats and content as annotations.
    /// </summary>
    public static JsonSchemaEvaluationOptions Default { get; } = new();

    /// <summary>
    /// Gets the output format of the result. The default is <see cref="JsonSchemaOutputFormat.Flag"/>.
    /// </summary>
    public JsonSchemaOutputFormat OutputFormat { get; init; }

    /// <summary>
    /// Gets a value indicating whether <c>format</c> asserts in dialects where it annotates by
    /// default. Formats the library does not know still pass. The 2020-12 format-assertion vocabulary
    /// asserts regardless and fails unknown formats. The default is <see langword="false"/>.
    /// </summary>
    public bool FormatAssertion { get; init; }

    /// <summary>
    /// Gets a value indicating whether <c>contentEncoding</c>, <c>contentMediaType</c> and
    /// <c>contentSchema</c> assert: <c>base64</c> content must decode, <c>application/json</c> content
    /// must parse, and parsed content must match <c>contentSchema</c>. The default is
    /// <see langword="false"/>, as the specification requires.
    /// </summary>
    public bool ContentAssertion { get; init; }

    /// <summary>
    /// Gets how deeply schemas may nest during one evaluation, counting every subschema and reference
    /// followed. The default is 512.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxEvaluationDepth
    {
        get => _maxEvaluationDepth;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxEvaluationDepth = value;
        }
    }

    /// <summary>
    /// Gets formats that add to or replace the built-in ones, matched by name. When two share a name,
    /// the later one is used. The list is copied when it is set.
    /// </summary>
    /// <exception cref="ArgumentNullException">An item of the value, or its name, is <see langword="null"/>.</exception>
    public IReadOnlyList<IJsonSchemaFormat>? Formats
    {
        get => _formats;
        init
        {
            if (value is null)
            {
                _formats = null;
                _formatsByName = null;
                return;
            }

            IJsonSchemaFormat[] formats = [.. value];
            Dictionary<string, IJsonSchemaFormat> byName = new(formats.Length, StringComparer.Ordinal);
            foreach (IJsonSchemaFormat format in formats)
            {
                ArgumentNullException.ThrowIfNull(format, nameof(value));
                ArgumentNullException.ThrowIfNull(format.Name, nameof(value));
                byName[format.Name] = format;
            }

            _formats = formats;
            _formatsByName = byName;
        }
    }

    /// <summary>Finds the caller's format of a name, which takes precedence over a built-in one.</summary>
    internal bool TryGetCustomFormat(string name, [NotNullWhen(true)] out IJsonSchemaFormat? format)
    {
        if (_formatsByName is not null && _formatsByName.TryGetValue(name, out format))
        {
            return true;
        }

        format = null;
        return false;
    }

    private sealed class DebugView
    {
        private readonly JsonSchemaEvaluationOptions _options;

        public DebugView(JsonSchemaEvaluationOptions options)
        {
            _options = options;
        }

        public JsonSchemaOutputFormat OutputFormat => _options.OutputFormat;

        public bool FormatAssertion => _options.FormatAssertion;

        public bool ContentAssertion => _options.ContentAssertion;

        public int MaxEvaluationDepth => _options.MaxEvaluationDepth;

        public IReadOnlyList<IJsonSchemaFormat>? Formats => _options.Formats;
    }
}
