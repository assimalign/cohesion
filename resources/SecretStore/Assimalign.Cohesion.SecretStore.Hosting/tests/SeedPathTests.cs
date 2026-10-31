using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting.Internal;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class SeedPathTests
{
    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - AddSecret: blank and reserved seed paths are refused at composition")]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("trusted-issuers.json")]
    public void AddSecret_WithInvalidSeedPath_ShouldRejectAtComposition(string path)
    {
        // Arrange
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        int registrationCount = builder.Services.Container.Count;

        // Act
        Should.Throw<ArgumentException>(() => builder.AddSecret(path, ReadOnlyMemory<byte>.Empty));

        // Assert
        builder.Services.Container.Count.ShouldBe(registrationCount);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - AddSecret: the root contract refuses a default seed path")]
    public void AddSecret_WithDefaultSeedPath_ShouldRejectBeforeRegistration()
    {
        // Arrange
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        ISecretStoreApplicationBuilder contract = builder;
        int registrationCount = builder.Services.Container.Count;

        // Act
        Should.Throw<ArgumentException>(() => contract.AddSecret(default, ReadOnlyMemory<byte>.Empty));

        // Assert
        builder.Services.Container.Count.ShouldBe(registrationCount);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - AddSecret: seed paths preserve the logical-path grammar")]
    [InlineData("certs/api")]
    [InlineData("Trusted-issuers.json")]
    [InlineData("../relative\\name")]
    [InlineData(" path ")]
    public void AddSecret_WithPermittedSeedPath_ShouldSnapshotAtExactPath(string path)
    {
        // Arrange
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        byte[] bytes = [1, 2, 3];

        // Act
        SecretStoreApplicationBuilder returned = builder.AddSecret(path, bytes);
        bytes[0] = 9;

        // Assert
        returned.ShouldBeSameAs(builder);
        SecretSeed seed = builder.Services.Container
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<SecretSeed>()
            .ShouldHaveSingleItem();
        seed.Path.ShouldBe(path);
        seed.Value.ToArray().ShouldBe([1, 2, 3]);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Repository: raw invalid seed paths are refused before persistence")]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("trusted-issuers.json")]
    public void Constructor_WithInvalidRawSeedPath_ShouldRejectBeforePersistence(string path)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string storePath = Path.Combine(directory.Path, "store");
        IDataProtectionProvider protection = DataProtectionProvider.Create(
            FileSystemKeyRepository.Create(Path.Combine(directory.Path, "key-ring")));
        var seeds = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
        {
            [path] = new byte[] { 1, 2, 3 },
        };

        // Act
        ArgumentException error = Should.Throw<ArgumentException>(() => new SecretStoreRepository(
            storePath, protection.CreateProtector("tests"), seeds));

        // Assert
        error.ParamName.ShouldBe("seeds");
        Directory.Exists(storePath).ShouldBeFalse();
    }
}
