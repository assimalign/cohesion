namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Represents character-based text content. The full text surface (encoding behavior, line and segment
/// access) is designed under the text content feature; this contract currently anchors the package's
/// dependency on the root content model.
/// </summary>
public interface ITextFile : IContent
{
}
