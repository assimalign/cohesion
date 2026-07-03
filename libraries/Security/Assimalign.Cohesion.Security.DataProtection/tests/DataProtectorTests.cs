using System;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

public class DataProtectorTests
{
    private static readonly byte[] Sample = Encoding.UTF8.GetBytes("cohesion-data-protection-sample-payload");

    private static IDataProtectionProvider CreateProvider(string discriminator = "app")
    {
        return DataProtectionProvider.Create(
            new InMemoryKeyRepository(),
            options => options.ApplicationDiscriminator = discriminator);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Protect: Should round-trip a payload")]
    public void Protect_ThenUnprotect_ShouldReturnOriginalPlaintext()
    {
        IDataProtector protector = CreateProvider().CreateProtector("purpose");

        byte[] protectedData = protector.Protect(Sample);
        byte[] recovered = protector.Unprotect(protectedData);

        recovered.ShouldBe(Sample);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Protect: Should round-trip an empty payload")]
    public void Protect_OnEmptyPlaintext_ShouldRoundTrip()
    {
        IDataProtector protector = CreateProvider().CreateProtector("purpose");

        byte[] recovered = protector.Unprotect(protector.Protect(ReadOnlySpan<byte>.Empty));

        recovered.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Protect: Should produce a fresh ciphertext each call")]
    public void Protect_OnRepeatedCalls_ShouldProduceDistinctPayloads()
    {
        IDataProtector protector = CreateProvider().CreateProtector("purpose");

        byte[] first = protector.Protect(Sample);
        byte[] second = protector.Protect(Sample);

        // Random nonce per call ⇒ distinct payloads that both still decrypt.
        first.ShouldNotBe(second);
        protector.Unprotect(first).ShouldBe(Sample);
        protector.Unprotect(second).ShouldBe(Sample);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Protect: Should embed a versioned key-id header")]
    public void Protect_ShouldEmitVersionAndKeyIdHeader()
    {
        IDataProtector protector = CreateProvider().CreateProtector("purpose");

        byte[] protectedData = protector.Protect(Sample);

        // [version:1][keyId:16][nonce:12][ciphertext][tag:16]
        protectedData.Length.ShouldBeGreaterThan(45);
        protectedData[0].ShouldBe((byte)0x01);
        // The key id is a non-zero GUID in bytes 1..17.
        Guid keyId = new(protectedData.AsSpan(1, 16));
        keyId.ShouldNotBe(Guid.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Unprotect: Should reject a tampered payload")]
    public void Unprotect_OnTamperedCiphertext_ShouldThrow()
    {
        IDataProtector protector = CreateProvider().CreateProtector("purpose");
        byte[] protectedData = protector.Protect(Sample);
        protectedData[^1] ^= 0xFF; // flip a tag byte

        Should.Throw<DataProtectionException>(() => protector.Unprotect(protectedData));
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Unprotect: Should reject a malformed payload")]
    public void Unprotect_OnMalformedPayload_ShouldThrow()
    {
        IDataProtector protector = CreateProvider().CreateProtector("purpose");

        Should.Throw<DataProtectionException>(() => protector.Unprotect(new byte[] { 1, 2, 3 }));
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Purpose: Should isolate different purposes")]
    public void Unprotect_OnDifferentPurpose_ShouldThrow()
    {
        IDataProtectionProvider provider = CreateProvider();
        IDataProtector purposeA = provider.CreateProtector("purpose-a");
        IDataProtector purposeB = provider.CreateProtector("purpose-b");

        byte[] protectedData = purposeA.Protect(Sample);

        Should.Throw<DataProtectionException>(() => purposeB.Unprotect(protectedData));
        purposeA.Unprotect(protectedData).ShouldBe(Sample);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Purpose: Should isolate different applications sharing a key ring")]
    public void Unprotect_OnDifferentDiscriminator_ShouldThrow()
    {
        // Both apps share the same repository (and therefore the same ring key), but their
        // discriminators fold into the subkey derivation, so payloads do not cross-validate.
        InMemoryKeyRepository shared = new();
        IDataProtector appOne = DataProtectionProvider
            .Create(shared, o => o.ApplicationDiscriminator = "app-one")
            .CreateProtector("purpose");
        IDataProtector appTwo = DataProtectionProvider
            .Create(shared, o => o.ApplicationDiscriminator = "app-two")
            .CreateProtector("purpose");

        byte[] protectedData = appOne.Protect(Sample);

        Should.Throw<DataProtectionException>(() => appTwo.Unprotect(protectedData));
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Purpose: Chained and params derivation should agree")]
    public void CreateProtector_ChainedAndParams_ShouldDeriveTheSameSubkey()
    {
        IDataProtectionProvider provider = CreateProvider();
        IDataProtector chained = provider.CreateProtector("outer").CreateProtector("inner");
        IDataProtector viaParams = provider.CreateProtector("outer", "inner");

        byte[] protectedData = chained.Protect(Sample);

        viaParams.Unprotect(protectedData).ShouldBe(Sample);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - Purpose: Sub-purpose should isolate from its parent")]
    public void CreateProtector_SubPurpose_ShouldNotUnprotectParentPayload()
    {
        IDataProtectionProvider provider = CreateProvider();
        IDataProtector parent = provider.CreateProtector("outer");
        IDataProtector child = parent.CreateProtector("inner");

        byte[] parentData = parent.Protect(Sample);

        Should.Throw<DataProtectionException>(() => child.Unprotect(parentData));
    }
}
