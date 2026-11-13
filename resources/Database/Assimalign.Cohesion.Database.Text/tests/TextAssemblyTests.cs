using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Text.Internal;

namespace Assimalign.Cohesion.Database.Text.Tests;

public sealed class TextAssemblyTests
{
    private readonly ITestOutputHelper _output;

    public TextAssemblyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Assembly: the independent child root references only Types within Database")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only assembly reference-table inspection; the test assembly is not trimmed.")]
    public void GetReferencedAssemblies_TextChildRoot_ReferencesOnlyTypes()
    {
        // Arrange / Act
        string?[] references = typeof(TextAnalyzer).Assembly.GetReferencedAssemblies().Select(name => name.Name)
            .Where(name => name is not null && name.StartsWith("Assimalign.Cohesion.", StringComparison.Ordinal)).ToArray();

        // Assert
        references.ShouldBe(new[] { "Assimalign.Cohesion.Database.Types" });
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Tokenizer: the entire scalar classification is pinned beside analyzer version 1")]
    public void IsLetterOrDigit_AllScalars_PreservesVersionedClassification()
    {
        // Arrange
        var classification = new byte[0x110000];

        // Act
        for (int scalar = 0; scalar < classification.Length; scalar++)
        {
            classification[scalar] = TextUnicodeData.IsLetterOrDigit(scalar) ? (byte)1
                : TextUnicodeData.IsContinuation(scalar) ? (byte)2 : (byte)0;
        }

        string hash = Convert.ToHexString(SHA256.HashData(classification));

        // Assert
        _output.WriteLine("Classification: " + hash);
        TextAnalyzerDescriptor.Simple.Version.ShouldBe(1);
        TextAnalyzerDescriptor.English.Version.ShouldBe(1);
        hash.ShouldBe("CB1B5F63C7050E70BDC89FFE829288DC26E04E072C544B4FE46F4797633A0752");
    }
}
