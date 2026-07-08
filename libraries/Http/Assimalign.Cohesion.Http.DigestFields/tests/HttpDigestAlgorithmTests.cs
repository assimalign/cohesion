using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.DigestFields.Tests;

using Assimalign.Cohesion.Http;

public class HttpDigestAlgorithmTests
{
    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Algorithm: Active algorithms are supported")]
    [InlineData("sha-256", 32)]
    [InlineData("sha-512", 64)]
    public void TryParse_ActiveAlgorithm_IsSupported(string key, int hashLength)
    {
        HttpDigestAlgorithm.TryParse(key, out HttpDigestAlgorithm algorithm).ShouldBeTrue();

        algorithm.IsRegistered.ShouldBeTrue();
        algorithm.IsSupported.ShouldBeTrue();
        algorithm.Key.ShouldBe(key);
        algorithm.HashLengthInBytes.ShouldBe(hashLength);
    }

    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Algorithm: Deprecated entries are recognized but not supported")]
    [InlineData("md5")]
    [InlineData("sha")]
    [InlineData("unixsum")]
    [InlineData("unixcksum")]
    public void TryParse_DeprecatedAlgorithm_IsRecognizedButUnsupported(string key)
    {
        HttpDigestAlgorithm.TryParse(key, out HttpDigestAlgorithm algorithm).ShouldBeTrue();

        algorithm.IsRegistered.ShouldBeTrue();
        algorithm.IsSupported.ShouldBeFalse();
        algorithm.HashLengthInBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Algorithm: Unregistered key is rejected")]
    public void TryParse_UnregisteredKey_ReturnsFalse()
    {
        HttpDigestAlgorithm.TryParse("crc32c", out HttpDigestAlgorithm algorithm).ShouldBeFalse();

        algorithm.IsRegistered.ShouldBeFalse();
        algorithm.IsSupported.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Algorithm: Key match is case-insensitive and canonicalizes")]
    public void TryParse_MixedCase_CanonicalizesToRegistryKey()
    {
        HttpDigestAlgorithm.TryParse("SHA-256", out HttpDigestAlgorithm algorithm).ShouldBeTrue();

        algorithm.Key.ShouldBe("sha-256");
        algorithm.ShouldBe(HttpDigestAlgorithm.Sha256);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Algorithm: Default value is unregistered and unsupported")]
    public void Default_IsUnregistered()
    {
        HttpDigestAlgorithm algorithm = default;

        algorithm.IsRegistered.ShouldBeFalse();
        algorithm.IsSupported.ShouldBeFalse();
        algorithm.Key.ShouldBeNull();
    }
}
