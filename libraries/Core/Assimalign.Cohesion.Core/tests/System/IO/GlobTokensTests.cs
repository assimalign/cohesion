using System;
using System.Collections;
using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace System.IO.Tests;

using static Glob;

/// <summary>
/// Pins <see cref="Glob.Tokens"/> as a read-only view that allocates nothing. Globs parsed from one
/// pattern share a token array through the parse cache, so a write through one glob's tokens would
/// change every glob of that pattern until the cache next clears.
/// </summary>
public class GlobTokensTests
{
    [Fact(DisplayName = "Cohesion Test [Core] - Glob.Tokens: Should refuse writes to a token array that globs share")]
    public void Tokens_WhenWrittenThroughCollectionInterfaces_ShouldThrowAndLeaveSharedArrayIntact()
    {
        // Arrange: two globs over one array, as the parse cache hands out for one pattern.
        string directory = $"/d{Guid.NewGuid():N}";
        string pattern = $"{directory}/file[0-9].txt";
        TokenBase[] shared = Parser.Tokenize(pattern);
        var original = (TokenBase[])shared.Clone();
        var written = new Glob(shared);
        var other = new Glob(shared);
        IList<Token> genericList = written.Tokens;
        IList list = written.Tokens;

        // Act & Assert
        Should.Throw<NotSupportedException>(() => genericList[0] = genericList[1]);
        Should.Throw<NotSupportedException>(() => list[0] = list[1]);
        shared.ShouldBe(original);
        other.ToString().ShouldBe(pattern);
        other.IsMatch($"{directory}/file5.txt").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Core] - Glob.Tokens: Should read the tokens without allocating")]
    public void Tokens_WhenRead_ShouldNotAllocate()
    {
        // Arrange
        Glob glob = Glob.Parse($"/d{Guid.NewGuid():N}/**/file[0-9].{{cs,txt}}");

        // Warms the JIT.
        CountTokens(glob);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = CountTokens(glob);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        count.ShouldBe(glob.Count);
        allocated.ShouldBe(0L);
    }

    private static int CountTokens(Glob glob)
    {
        int count = 0;

        foreach (Token _ in glob.Tokens)
        {
            count++;
        }

        return count;
    }
}
