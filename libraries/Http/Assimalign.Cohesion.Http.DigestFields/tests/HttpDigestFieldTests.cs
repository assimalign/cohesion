using System;
using System.Security.Cryptography;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.DigestFields.Tests;

using Assimalign.Cohesion.Http;

public class HttpDigestFieldTests
{
    // Known-answer vector: base64(SHA-256("")) — a widely published constant.
    private const string EmptySha256Base64 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=";

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Field: Parses a single sha-256 entry and round-trips")]
    public void TryParse_SingleSha256_RoundTrips()
    {
        string value = $"sha-256=:{EmptySha256Base64}:";

        HttpDigestField.TryParse(value, out HttpDigestField field).ShouldBeTrue();

        field.Entries.Count.ShouldBe(1);
        field.Entries[0].Algorithm.ShouldBe(HttpDigestAlgorithm.Sha256);
        field.HasSupportedAlgorithm.ShouldBeTrue();
        field.Serialize().ShouldBe(value);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Field: Preserves all entries of a multi-algorithm value")]
    public void TryParse_MultiAlgorithm_PreservesAllEntries()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello world");
        string sha256 = Convert.ToBase64String(SHA256.HashData(content));
        string sha512 = Convert.ToBase64String(SHA512.HashData(content));
        string value = $"sha-256=:{sha256}:, sha-512=:{sha512}:";

        HttpDigestField.TryParse(value, out HttpDigestField field).ShouldBeTrue();

        field.Entries.Count.ShouldBe(2);
        field.Entries[0].Algorithm.ShouldBe(HttpDigestAlgorithm.Sha256);
        field.Entries[1].Algorithm.ShouldBe(HttpDigestAlgorithm.Sha512);
        field.Serialize().ShouldBe(value);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Field: Recognizes a deprecated algorithm but reports no supported algorithm")]
    public void TryParse_DeprecatedOnly_RecognizedButUnsupported()
    {
        // md5 is recognized on parse but never usable for verification (RFC 9530 §5).
        HttpDigestField.TryParse("md5=:1B2M2Y8AsgTpgAmY7PhCfg==:", out HttpDigestField field).ShouldBeTrue();

        field.Entries.Count.ShouldBe(1);
        field.Entries[0].Algorithm.ShouldBe(HttpDigestAlgorithm.Md5);
        field.Entries[0].Algorithm.IsSupported.ShouldBeFalse();
        field.HasSupportedAlgorithm.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Field: Malformed values are rejected with a diagnostic")]
    [InlineData("sha-256=:not valid base64!:")] // bad base64
    [InlineData("sha-256=X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=")] // missing colons (not a byte sequence)
    [InlineData("sha-256=12345")] // wrong item type (integer, not byte sequence)
    [InlineData("")] // empty
    [InlineData("sha-256")] // bare key, no value
    public void TryParse_Malformed_ReturnsFalseWithError(string value)
    {
        HttpDigestField.TryParse(value, out HttpDigestField field, out string? error).ShouldBeFalse();

        field.Entries.Count.ShouldBe(0);
        error.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Field: Parse throws HttpDigestException on malformed input")]
    public void Parse_Malformed_Throws()
    {
        Should.Throw<HttpDigestException>(() => HttpDigestField.Parse("sha-256=12345"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Field: ForContent computes the correct sha-256 digest and wire form")]
    public void ForContent_Sha256_MatchesBclAndWireFormat()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello world");
        byte[] expected = SHA256.HashData(content);

        HttpDigestField field = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256);

        field.TryGetDigest(HttpDigestAlgorithm.Sha256, out ReadOnlyMemory<byte> digest).ShouldBeTrue();
        digest.ToArray().ShouldBe(expected);
        field.Serialize().ShouldBe($"sha-256=:{Convert.ToBase64String(expected)}:");
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Field: ForContent rejects an unsupported algorithm")]
    public void ForContent_UnsupportedAlgorithm_Throws()
    {
        Should.Throw<ArgumentException>(() => HttpDigestField.ForContent(new byte[] { 1 }, HttpDigestAlgorithm.Md5));
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verify: Matching content verifies")]
    public void VerifyContent_Match_ReturnsMatched()
    {
        byte[] content = Encoding.UTF8.GetBytes("the quick brown fox");
        HttpDigestField field = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256, HttpDigestAlgorithm.Sha512);

        field.VerifyContent(content).ShouldBe(HttpDigestVerificationResult.Matched);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verify: Tampered content mismatches")]
    public void VerifyContent_Tampered_ReturnsMismatched()
    {
        byte[] content = Encoding.UTF8.GetBytes("the quick brown fox");
        HttpDigestField field = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256);

        byte[] tampered = Encoding.UTF8.GetBytes("the quick brown FOX");
        field.VerifyContent(tampered).ShouldBe(HttpDigestVerificationResult.Mismatched);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verify: Known sha-256 vector over empty content matches")]
    public void VerifyContent_KnownEmptyVector_Matches()
    {
        HttpDigestField field = HttpDigestField.Parse($"sha-256=:{EmptySha256Base64}:");

        field.VerifyContent(ReadOnlySpan<byte>.Empty).ShouldBe(HttpDigestVerificationResult.Matched);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verify: A deprecated-only field cannot be verified")]
    public void VerifyContent_DeprecatedOnly_ReturnsNoSupportedAlgorithm()
    {
        HttpDigestField.TryParse("md5=:1B2M2Y8AsgTpgAmY7PhCfg==:", out HttpDigestField field).ShouldBeTrue();

        field.VerifyContent(ReadOnlySpan<byte>.Empty).ShouldBe(HttpDigestVerificationResult.NoSupportedAlgorithm);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verify: A wrong-length digest mismatches instead of throwing")]
    public void VerifyContent_WrongLengthDigest_ReturnsMismatched()
    {
        // A syntactically valid but too-short sha-256 digest must be treated as a mismatch.
        HttpDigestField.TryParse("sha-256=:AAAA:", out HttpDigestField field).ShouldBeTrue();

        field.VerifyContent(ReadOnlySpan<byte>.Empty).ShouldBe(HttpDigestVerificationResult.Mismatched);
    }
}
