using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// The registry behind <see cref="IHttpContentSerializationFeature"/>: a builder-time singleton
/// seeded onto every exchange. Registrations mutate copy-on-write arrays under a lock so the
/// per-request read path is lock-free; mutation happens only during composition.
/// </summary>
internal sealed class HttpContentSerializationFeature : IHttpContentSerializationFeature
{
    private readonly object _gate = new();
    private IHttpContentReader[] _readers = [];
    private IHttpContentWriter[] _writers = [];

    /// <inheritdoc />
    public string Name => nameof(HttpContentSerializationFeature);

    /// <inheritdoc />
    public IReadOnlyList<IHttpContentReader> Readers => _readers;

    /// <inheritdoc />
    public IReadOnlyList<IHttpContentWriter> Writers => _writers;

    internal void AddReader(IHttpContentReader reader)
    {
        lock (_gate)
        {
            _readers = [.. _readers, reader];
        }
    }

    internal void AddWriter(IHttpContentWriter writer)
    {
        lock (_gate)
        {
            _writers = [.. _writers, writer];
        }
    }

    /// <inheritdoc />
    public IHttpContentReader? GetReader(HttpMediaType mediaType)
    {
        IHttpContentReader[] readers = _readers;
        IHttpContentReader? match = null;
        int matchSpecificity = -1;

        foreach (IHttpContentReader reader in readers)
        {
            foreach (HttpMediaType range in reader.MediaTypes)
            {
                // Strictly-greater keeps the earliest registration on specificity ties.
                if (range.Includes(mediaType) && range.Specificity > matchSpecificity)
                {
                    match = reader;
                    matchSpecificity = range.Specificity;
                }
            }
        }

        return match;
    }

    /// <inheritdoc />
    public IHttpContentWriter? GetWriter(HttpMediaType mediaType)
    {
        IHttpContentWriter[] writers = _writers;
        IHttpContentWriter? match = null;
        int matchSpecificity = -1;

        foreach (IHttpContentWriter writer in writers)
        {
            foreach (HttpMediaType range in writer.MediaTypes)
            {
                if (range.Includes(mediaType) && range.Specificity > matchSpecificity)
                {
                    match = writer;
                    matchSpecificity = range.Specificity;
                }
            }
        }

        return match;
    }
}
