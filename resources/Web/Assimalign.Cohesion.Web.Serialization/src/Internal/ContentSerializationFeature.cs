using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// The registry behind <see cref="IContentSerializationFeature"/>: an application singleton
/// seeded onto every exchange. <see cref="ContentSerializationBuilder.Build"/> hands it a snapshot of
/// the registrations, so the registry is immutable and the per-request read path takes no lock.
/// </summary>
internal sealed class ContentSerializationFeature : IContentSerializationFeature
{
    private readonly IContentReader[] _readers;
    private readonly IContentWriter[] _writers;

    internal ContentSerializationFeature(IContentReader[] readers, IContentWriter[] writers)
    {
        _readers = readers;
        _writers = writers;
    }

    /// <inheritdoc />
    public string Name => nameof(ContentSerializationFeature);

    /// <inheritdoc />
    public IReadOnlyList<IContentReader> Readers => _readers;

    /// <inheritdoc />
    public IReadOnlyList<IContentWriter> Writers => _writers;

    /// <inheritdoc />
    public IContentReader? GetReader(HttpMediaType mediaType)
    {
        IContentReader? match = null;
        int matchSpecificity = -1;

        foreach (IContentReader reader in _readers)
        {
            IReadOnlyList<HttpMediaType> mediaTypes = reader.MediaTypes;
            for (int i = 0; i < mediaTypes.Count; i++)
            {
                HttpMediaType range = mediaTypes[i];
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
    public IContentWriter? GetWriter(HttpMediaType mediaType)
    {
        IContentWriter? match = null;
        int matchSpecificity = -1;

        foreach (IContentWriter writer in _writers)
        {
            IReadOnlyList<HttpMediaType> mediaTypes = writer.MediaTypes;
            for (int i = 0; i < mediaTypes.Count; i++)
            {
                HttpMediaType range = mediaTypes[i];
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
