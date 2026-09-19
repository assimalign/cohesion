using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Types.Tests;

/// <summary>Verifies the pinned Unicode transforms, byte ordering, and globalization independence.</summary>
public class CollationUnicodeTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Types] - Collation: pinned Unicode transformations define canonical keys")]
    [InlineData(0, "Alice\0É\U00010400", "Alice\0É\U00010400")]
    [InlineData(2, "Alice", "alice")]
    [InlineData(2, "ÉCOLE", "école")]
    [InlineData(2, "Σσς", "σσσ")]
    [InlineData(2, "Kſµ", "ksμ")]
    [InlineData(2, "Iİı", "iİı")]
    [InlineData(2, "ẞß", "ßß")]
    [InlineData(2, "ﬃ", "ﬃ")]
    [InlineData(2, "\U00010400\U00010428", "\U00010428\U00010428")]
    [InlineData(2, "\U0001E900", "\U0001E922")]
    [InlineData(3, "ÉCOLE", "ecole")]
    [InlineData(3, "E\u0301COLE", "ecole")]
    [InlineData(3, "Ǻ\u0327", "a")]
    [InlineData(3, "Iİı", "iiı")]
    [InlineData(3, "\u0301\u0903\u20DD", "")]
    [InlineData(3, "가각", "\u1100\u1161\u1100\u1161\u11A8")]
    [InlineData(3, "\U0001D15E", "\U0001D157")]
    [InlineData(3, "øæﬃ", "øæﬃ")]
    public void Normalize_PinnedUnicode_ShouldProduceExpectedBytes(byte id, string input, string expected)
    {
        // Arrange
        Collation collation = Collation.FromId(id);

        // Act / Assert: equality, canonical text, byte keys, and hashes share one transform.
        collation.IsIndexBacked.ShouldBeTrue();
        collation.Normalize(input).ShouldBe(expected);
        collation.Normalize(expected).ShouldBe(expected);
        collation.GetSortKey(input).ShouldBe(Encoding.UTF8.GetBytes(expected));
        collation.Compare(input, expected).ShouldBe(0);
        collation.GetHashCode(input).ShouldBe(collation.GetHashCode(expected));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Types] - Collation: comparison and escaped index byte ordering agree")]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void Compare_ByteTransforms_ShouldAgreeForPrefixesMarksAndSupplementaryScalars(byte id)
    {
        // Arrange
        Collation collation = Collation.FromId(id);
        string[] corpus = ["", "\0", "a", "A", "alice", "Alice", "a\0", "a\0b", "ab", "é", "e\u0301", "Σ", "ς", "\uE000", "\U00010400", "\U00010428", "\U0001F600"];

        foreach (string left in corpus)
        {
            foreach (string right in corpus)
            {
                byte[] leftKey = new DatabaseKeyWriter().AppendString(left, collation).ToArray();
                byte[] rightKey = new DatabaseKeyWriter().AppendString(right, collation).ToArray();

                // Assert: raw bytes are the complete tree comparison, including equal folds.
                Math.Sign(leftKey.AsSpan().SequenceCompareTo(rightKey))
                    .ShouldBe(Math.Sign(collation.Compare(left, right)));
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Types] - Collation: invalid Unicode is rejected without key collisions")]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void Normalize_UnpairedSurrogate_ShouldReject(byte id)
    {
        Collation collation = Collation.FromId(id);
        Should.Throw<DatabaseTypeException>(() => collation.Normalize("\uD800"));
        Should.Throw<DatabaseTypeException>(() => collation.GetSortKey("\uDC00"));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Collation: invariant-globalization child process preserves byte collations")]
    public async Task Normalize_InvariantGlobalizationProcess_ShouldPreserveUnicodeSemanticsAsync()
    {
        // Arrange: run the normative cases in a fresh CLR; globalization is fixed at startup.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(SourcePath())!, "Assimalign.Cohesion.Database.Types.Tests.csproj"));
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name);
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName~Normalize_PinnedUnicode|FullyQualifiedName~Compare_ByteTransforms|FullyQualifiedName~InvariantGlobalization_LegacyCollation");
        start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        // Act
        using var process = Process.Start(start)!;
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errorsTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string output = await outputTask;
        string errors = await errorsTask;

        // Assert
        process.ExitCode.ShouldBe(0, output + errors);
        output.ShouldContain("Passed", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Collation: legacy linguistic operations fail loudly under invariant globalization")]
    public void InvariantGlobalization_LegacyCollation_ShouldRejectInsteadOfChangingMeaning()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") == "1")
        {
            Should.Throw<DatabaseTypeException>(() => Collation.Invariant.Compare("apple", "Apple"));
            Should.Throw<DatabaseTypeException>(() => Collation.Invariant.GetHashCode("apple"));
        }
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
