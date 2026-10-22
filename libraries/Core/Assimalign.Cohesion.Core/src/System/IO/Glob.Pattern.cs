using System;

namespace System.IO;

public sealed partial class Glob
{
    // Deviates from the repo internal-type placement rule per owner request: keep pattern metadata nested in Glob.
    /// <summary>Immutable tokens and length bounds shared by cached parses of one pattern.</summary>
    internal sealed class GlobPattern
    {
        public GlobPattern(TokenBase[] tokens)
        {
            Tokens = ArgumentNullException.ThrowIfNullOrNone(tokens);
            for (int i = 0; i < tokens.Length; i++)
            {
                MinimumLength += tokens[i].ConsumesMinLength;
                ConsumesVariableLength |= tokens[i].ConsumesVariableLength;
            }
        }

        public TokenBase[] Tokens { get; }

        public int MinimumLength { get; }

        public bool ConsumesVariableLength { get; }
    }
}
