using System;

using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

public static partial class Mp4Profile
{
    /// <summary>Checks for a movie-extends declaration or movie-fragment boxes, including an initialization-only file.</summary>
    /// <param name="document">The native document.</param>
    /// <returns>Whether the file declares or contains fragmented media.</returns>
    /// <exception cref="ArgumentNullException">The document is null.</exception>
    public static bool IsFragmented(BmffDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var box in document.Boxes)
        {
            if (box is MovieFragmentBox || box is MovieBox movie && Mp4Tree.Child<MovieExtensionBox>(movie.Children) is not null) { return true; }
        }
        return false;
    }

    /// <summary>Moves moov before the first mdat and relocates offsets through BMFF's transactional operation.</summary>
    /// <remarks>Progressive local-data files only. Save to a separate output and reload before querying samples again.
    /// This operation does not remux progressive media into fragments.</remarks>
    /// <param name="document">A document loaded with the MP4 profile.</param>
    /// <exception cref="ArgumentNullException">The document is null.</exception>
    /// <exception cref="ContentFormatException">The layout is invalid or its relocation is unsupported.</exception>
    public static void FastStart(BmffDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (IsFragmented(document))
        {
            throw new ContentFormatException(ErrorMessages.FastStartUnsupported, ContentFormatErrorCode.Unsupported, 0);
        }
        var movie = Mp4Tree.Child<MovieBox>(document.Boxes) ??
            throw new ContentFormatException(ErrorMessages.RequiredBoxMissing, ContentFormatErrorCode.InvalidStructure, 0);
        var movieIndex = document.Boxes.IndexOf(movie);
        var mediaIndex = -1;
        for (var i = 0; i < document.Boxes.Count; i++)
        {
            if (document.Boxes[i] is MediaDataBox) { mediaIndex = i; break; }
        }
        if (mediaIndex < 0 || movieIndex < mediaIndex) { return; }
        foreach (var child in movie.Children)
        {
            if (child is not TrackBox track) { continue; }
            var info = Mp4Tree.Required<MediaInfoBox>(Mp4Tree.Required<MediaBox>(track));
            var descriptions = Mp4Tree.Required<SampleDescriptionBox>(Mp4Tree.Required<SampleTableBox>(info));
            foreach (var entry in descriptions.Children)
            {
                if (!Mp4File.IsLocalEntry(entry, info))
                {
                    throw new ContentFormatException(ErrorMessages.FastStartUnsupported, ContentFormatErrorCode.Unsupported, entry.Offset);
                }
            }
        }
        // New documents need their caller-authored initial coordinates checkpointed first.
        document.RelocateMediaOffsets();
        document.Boxes.RemoveAt(movieIndex);
        document.Boxes.Insert(mediaIndex, movie);
        try { document.RelocateMediaOffsets(); }
        catch
        {
            document.Boxes.RemoveAt(mediaIndex);
            document.Boxes.Insert(movieIndex, movie);
            throw;
        }
    }
}
