using System;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

public class KeyRingRotationTests
{
    private static readonly byte[] Sample = Encoding.UTF8.GetBytes("rotation-sample");
    private static readonly DateTimeOffset Origin = new(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);

    private static DataProtectionOptions Options() => new()
    {
        ApplicationDiscriminator = "app",
        KeyLifetime = TimeSpan.FromMinutes(10),
        UnprotectGracePeriod = TimeSpan.FromMinutes(5),
    };

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should reuse the active key within its lifetime")]
    public void Protect_WithinActiveWindow_ShouldNotCreateAdditionalKeys()
    {
        InMemoryKeyRepository repository = new();
        MutableTimeProvider time = new(Origin);
        IDataProtector protector = DataProtectionProvider
            .Create(Options(), repository, time)
            .CreateProtector("purpose");

        protector.Protect(Sample);
        time.Advance(TimeSpan.FromMinutes(5));
        protector.Protect(Sample);

        repository.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should rotate to a new key after expiry")]
    public void Protect_AfterExpiry_ShouldCreateAndUseANewKey()
    {
        InMemoryKeyRepository repository = new();
        MutableTimeProvider time = new(Origin);
        IDataProtector protector = DataProtectionProvider
            .Create(Options(), repository, time)
            .CreateProtector("purpose");

        byte[] first = protector.Protect(Sample);
        time.Advance(TimeSpan.FromMinutes(11)); // past the 10-minute lifetime
        byte[] second = protector.Protect(Sample);

        repository.Count.ShouldBe(2);
        // Different producing keys ⇒ different key id in the header.
        Guid firstKeyId = new(first.AsSpan(1, 16));
        Guid secondKeyId = new(second.AsSpan(1, 16));
        secondKeyId.ShouldNotBe(firstKeyId);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should unprotect a retired key within the grace window")]
    public void Unprotect_RetiredKeyWithinGrace_ShouldSucceed()
    {
        InMemoryKeyRepository repository = new();
        MutableTimeProvider time = new(Origin);
        IDataProtector protector = DataProtectionProvider
            .Create(Options(), repository, time)
            .CreateProtector("purpose");

        byte[] protectedByKey1 = protector.Protect(Sample);

        // Rotate: key1 expires at +10m; the grace window runs to +15m.
        time.Advance(TimeSpan.FromMinutes(11));
        protector.Protect(Sample); // forces key2 creation
        time.Set(Origin + TimeSpan.FromMinutes(14)); // still inside key1's grace window

        protector.Unprotect(protectedByKey1).ShouldBe(Sample);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should reject a key aged out of the grace window")]
    public void Unprotect_RetiredKeyBeyondGrace_ShouldThrow()
    {
        InMemoryKeyRepository repository = new();
        MutableTimeProvider time = new(Origin);
        IDataProtector protector = DataProtectionProvider
            .Create(Options(), repository, time)
            .CreateProtector("purpose");

        byte[] protectedByKey1 = protector.Protect(Sample);

        time.Advance(TimeSpan.FromMinutes(11));
        protector.Protect(Sample); // key2
        time.Set(Origin + TimeSpan.FromMinutes(16)); // past key1 expiry (+10m) + grace (+5m)

        Should.Throw<DataProtectionException>(() => protector.Unprotect(protectedByKey1));
    }
}
