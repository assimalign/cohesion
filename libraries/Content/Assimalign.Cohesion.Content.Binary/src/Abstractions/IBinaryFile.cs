namespace Assimalign.Cohesion.Content.Binary;

/// <summary>
/// Represents structured binary content. The shared binary surface (exact reads, bounded slices) is
/// designed under the parser-primitives feature; this contract currently anchors the package's
/// dependency on the root content model.
/// </summary>
public interface IBinaryFile : IContent
{
}
