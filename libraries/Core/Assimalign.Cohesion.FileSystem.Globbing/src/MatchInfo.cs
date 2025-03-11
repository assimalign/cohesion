using System.Runtime.CompilerServices;
using Assimalign.Cohesion.FileSystem.Globbing.Tokens;

namespace Assimalign.Cohesion.FileSystem.Globbing
{
    public class MatchInfo
    {
        public GlobTokenMatch[] Matches { get; set; }

        public GlobToken Missed { get; set; }

        public bool Success { get; set; }

        public string UnmatchedText { get; set; }

    }
}