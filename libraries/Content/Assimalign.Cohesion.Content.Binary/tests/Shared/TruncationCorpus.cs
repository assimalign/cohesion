// Linked-source precedent: .claude/rules/testing.md. Downstream Content tests may use:
// <Compile Include="$(CohesionRepositoryDirectory)libraries\Content\Assimalign.Cohesion.Content.Binary\tests\Shared\*.cs" Link="Shared\Binary\%(Filename)%(Extension)" />
// Deviates from the consuming RootNamespace rule for linked test support; instances never cross assemblies.
using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal static class TruncationCorpus
{
    // The parser returns true for an element and false for clean "no element".
    internal static void AssertPrefixes(byte[] valid, Func<ReadOnlyMemory<byte>, bool> parse, bool allowEmpty = false)
    {
        parse(valid).ShouldBeTrue();
        for (var length = 0; length < valid.Length; length++)
        {
            var prefix = valid.AsMemory(0, length);
            if (length == 0 && allowEmpty)
            {
                parse(prefix).ShouldBeFalse();
            }
            else
            {
                var error = Should.Throw<ContentFormatException>(() => parse(prefix));
                error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
            }
        }
    }

    internal static async ValueTask AssertPrefixesAsync(
        byte[] valid,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> parse,
        bool allowEmpty = false,
        CancellationToken cancellationToken = default)
    {
        (await parse(valid, cancellationToken)).ShouldBeTrue();
        for (var length = 0; length < valid.Length; length++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = valid.AsMemory(0, length);
            if (length == 0 && allowEmpty)
            {
                (await parse(prefix, cancellationToken)).ShouldBeFalse();
            }
            else
            {
                var error = await Should.ThrowAsync<ContentFormatException>(() => parse(prefix, cancellationToken).AsTask());
                error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
            }
        }
    }
}
