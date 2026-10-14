using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// The registry behind <see cref="IHttpContentSerializationFeature"/>: an application singleton
/// seeded onto every exchange. <see cref="ContentSerializationBuilder.Build"/> hands it a snapshot of
/// the registrations, so the registry is immutable and the per-request read path takes no lock.
/// </summary>
internal sealed class HttpContentSerializationFeature : IHttpContentSerializationFeature
{
    private readonly IHttpContentReader[] _readers;
    private readonly IHttpContentWriter[] _writers;

    internal HttpContentSerializationFeature(IHttpContentReader[] readers, IHttpContentWriter[] writers)
    {
        _readers = readers;
        _writers = writers;
    }

    /// <inheritdoc />
    public string Name => nameof(HttpContentSerializationFeature);

    /// <inheritdoc />
    public IReadOnlyList<IHttpContentReader> Readers => _readers;

    /// <inheritdoc />
    public IReadOnlyList<IHttpContentWriter> Writers => _writers;

    /// <inheritdoc />
    public IHttpContentReader? GetReader(HttpMediaType mediaType)
    {
        IHttpContentReader? match = null;
        int matchSpecificity = -1;

        foreach (IHttpContentReader reader in _readers)
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
        IHttpContentWriter? match = null;
        int matchSpecificity = -1;

        foreach (IHttpContentWriter writer in _writers)
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
