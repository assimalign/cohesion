using System.IO;

namespace Assimalign.Cohesion.Content.Text.Tests;

// Authored hostile fixture: generates an unterminated line without allocating the source.
internal sealed class EndlessTextReader : TextReader
{
    internal long CharactersRead { get; private set; }
    internal bool IsDisposed { get; private set; }

    public override int Read()
    {
        CharactersRead++;
        return 'x';
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
