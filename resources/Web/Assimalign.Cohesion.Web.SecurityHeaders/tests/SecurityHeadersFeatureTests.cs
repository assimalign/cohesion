using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// The default nonce feature: 128 bits of CSPRNG output in base64, fixed for the exchange once read, and
/// distinct across exchanges.
/// </summary>
public class SecurityHeadersFeatureTests
{
    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Feature: The nonce should be 128 bits of base64 and stable once read")]
    public void Nonce_ReadTwice_ShouldReturnTheSameBase64Value()
    {
        // Arrange
        SecurityHeadersFeature feature = new();

        // Act
        string first = feature.Nonce;
        string second = feature.Nonce;

        // Assert
        second.ShouldBeSameAs(first);
        Convert.FromBase64String(first).Length.ShouldBe(16);
        SecurityHeadersGrammar.IsBase64Value(first).ShouldBeTrue();
        feature.Name.ShouldBe(nameof(ISecurityHeadersFeature));
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Feature: Concurrent first reads should agree on one nonce")]
    public async Task Nonce_ConcurrentFirstReads_ShouldAgree()
    {
        // Arrange
        SecurityHeadersFeature feature = new();
        Task<string>[] reads = new Task<string>[16];

        // Act
        for (int index = 0; index < reads.Length; index++)
        {
            reads[index] = Task.Run(() => feature.Nonce);
        }

        string[] nonces = await Task.WhenAll(reads);

        // Assert
        new HashSet<string>(nonces).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.SecurityHeaders] - Feature: Separate exchanges should get distinct nonces")]
    public void Nonce_SeparateFeatures_ShouldDiffer()
    {
        // Arrange
        HashSet<string> nonces = new();

        // Act
        for (int index = 0; index < 64; index++)
        {
            nonces.Add(new SecurityHeadersFeature().Nonce);
        }

        // Assert
        nonces.Count.ShouldBe(64);
    }
}
