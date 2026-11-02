using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Assimalign.Cohesion.SourceGeneration.Tests;

/// <summary>
/// An additional file as the gateway SDK passes one for a resource project reference: its path is
/// the referenced project's, and only its metadata matters.
/// </summary>
internal sealed class TestAdditionalText : AdditionalText
{
    public TestAdditionalText(string path)
    {
        Path = path;
    }

    public override string Path { get; }

    public override SourceText? GetText(CancellationToken cancellationToken = default) => SourceText.From(string.Empty);
}
