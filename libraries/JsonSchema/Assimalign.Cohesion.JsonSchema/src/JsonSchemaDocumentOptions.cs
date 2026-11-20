using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// Options for loading a <see cref="JsonSchemaDocument"/>.
/// </summary>
[DebuggerDisplay("BaseUri = {BaseUri}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaDocumentOptions
{
    private readonly Uri? _baseUri;
    private readonly TimeSpan _regexMatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the options every load uses when none are given.
    /// </summary>
    public static JsonSchemaDocumentOptions Default { get; } = new();

    /// <summary>
    /// Gets the URI the document was retrieved from: its initial base URI (2020-12 Core §9.1.1).
    /// </summary>
    /// <remarks>
    /// A root <c>$id</c> is resolved against it, and the document stays reachable at it even when the
    /// root <c>$id</c> names another URI. A fragment is ignored. When neither this nor a root
    /// <c>$id</c> identifies the document, it gets a unique <c>urn:uuid:</c> base that is not reported.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not an absolute URI.</exception>
    public Uri? BaseUri
    {
        get => _baseUri;
        init
        {
            if (value is not null && !value.IsAbsoluteUri)
            {
                throw new ArgumentException(ErrorMessages.UriNotAbsolute, nameof(value));
            }

            _baseUri = value;
        }
    }

    /// <summary>
    /// Gets the dialect for a document whose root has no <c>$schema</c>, or <see langword="null"/>
    /// for <see cref="JsonSchemaDialect.Draft202012"/>.
    /// </summary>
    public JsonSchemaDialect? DefaultDialect { get; init; }

    /// <summary>
    /// Gets the registry the document registers its schema resources in and resolves references
    /// against, or <see langword="null"/> for a new registry of its own.
    /// </summary>
    public JsonSchemaRegistry? Registry { get; init; }

    /// <summary>
    /// Gets how long one <c>pattern</c> or <c>patternProperties</c> match may run before evaluation
    /// fails with <see cref="JsonSchemaErrorCode.RegularExpressionTimeout"/>. The default is two
    /// seconds. Patterns without backreferences, lookarounds or <c>\b</c> match in time linear in the
    /// input, so only those that need backtracking can be made slow by a crafted instance.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan RegexMatchTimeout
    {
        get => _regexMatchTimeout;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _regexMatchTimeout = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the document is validated against its dialect's meta-schema
    /// while it loads. A document that does not conform fails with
    /// <see cref="JsonSchemaErrorCode.MetaSchemaViolation"/>. The default is <see langword="false"/>.
    /// </summary>
    public bool ValidateSchema { get; init; }

    private sealed class DebugView
    {
        private readonly JsonSchemaDocumentOptions _options;

        public DebugView(JsonSchemaDocumentOptions options)
        {
            _options = options;
        }

        public Uri? BaseUri => _options.BaseUri;

        public JsonSchemaDialect? DefaultDialect => _options.DefaultDialect;

        public JsonSchemaRegistry? Registry => _options.Registry;

        public TimeSpan RegexMatchTimeout => _options.RegexMatchTimeout;

        public bool ValidateSchema => _options.ValidateSchema;
    }
}
