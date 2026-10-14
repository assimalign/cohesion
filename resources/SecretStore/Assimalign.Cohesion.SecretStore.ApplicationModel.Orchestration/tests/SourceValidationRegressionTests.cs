using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>Regressions for delivery validation that previously differed from declaration validation.</summary>
public sealed class SourceValidationRegressionTests
{
    /// <summary>Malformed resource prefixes and blank parameter names never reach source resolution.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - ResolveAsync: rejects malformed source before resolution")]
    [InlineData("store$:key")]
    [InlineData("storé:key")]
    [InlineData("parameter:   ")]
    public async Task ResolveAsync_InvalidSourceGrammar_ShouldRejectBeforeResolution(string source)
    {
        // Arrange
        var sources = new StubSourceResolver().Resolve(source, [1]);
        var declared = new ResourceCommandInput("command-key", "secretstore.add-secret", "appa", "key",
            Encoding.UTF8.GetBytes("{\"path\":\"key\",\"source\":\"" + source + "\"}"));

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() =>
            new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, CancellationToken.None).AsTask());

        // Assert
        sources.Calls.ShouldBeEmpty();
    }
}
