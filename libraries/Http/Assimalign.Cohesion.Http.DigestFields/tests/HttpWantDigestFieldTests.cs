using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.DigestFields.Tests;

using Assimalign.Cohesion.Http;

public class HttpWantDigestFieldTests
{
    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Want: Parses integer preferences and round-trips")]
    public void TryParse_IntegerPreferences_RoundTrips()
    {
        string value = "sha-256=3, sha-512=10";

        HttpWantDigestField.TryParse(value, out HttpWantDigestField field).ShouldBeTrue();

        field.Preferences.Count.ShouldBe(2);
        field.Preferences[0].Algorithm.ShouldBe(HttpDigestAlgorithm.Sha256);
        field.Preferences[0].Preference.ShouldBe(3);
        field.Preferences[1].Preference.ShouldBe(10);
        field.Serialize().ShouldBe(value);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Want: Selects the highest-preference supported algorithm")]
    public void TrySelectPreferred_HighestPreference_Wins()
    {
        HttpWantDigestField.TryParse("sha-256=3, sha-512=10", out HttpWantDigestField field).ShouldBeTrue();

        field.TrySelectPreferred(out HttpDigestAlgorithm algorithm).ShouldBeTrue();
        algorithm.ShouldBe(HttpDigestAlgorithm.Sha512);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Want: Breaks a preference tie toward the stronger algorithm")]
    public void TrySelectPreferred_Tie_PrefersStronger()
    {
        HttpWantDigestField.TryParse("sha-256=5, sha-512=5", out HttpWantDigestField field).ShouldBeTrue();

        field.TrySelectPreferred(out HttpDigestAlgorithm algorithm).ShouldBeTrue();
        algorithm.ShouldBe(HttpDigestAlgorithm.Sha512);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Want: Skips unacceptable (0) and deprecated algorithms")]
    public void TrySelectPreferred_SkipsUnacceptableAndDeprecated()
    {
        // sha-512 not acceptable (0); md5 deprecated (unsupported); only sha-256 is selectable.
        HttpWantDigestField.TryParse("sha-512=0, md5=9, sha-256=1", out HttpWantDigestField field).ShouldBeTrue();

        field.TrySelectPreferred(out HttpDigestAlgorithm algorithm).ShouldBeTrue();
        algorithm.ShouldBe(HttpDigestAlgorithm.Sha256);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Want: Returns false when no supported algorithm is acceptable")]
    public void TrySelectPreferred_NoneAcceptable_ReturnsFalse()
    {
        HttpWantDigestField.TryParse("sha-256=0, md5=9", out HttpWantDigestField field).ShouldBeTrue();

        field.TrySelectPreferred(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Want: Create builds a serializable preference field")]
    public void Create_BuildsSerializableField()
    {
        HttpWantDigestField field = HttpWantDigestField.Create(
            new HttpWantDigestPreference(HttpDigestAlgorithm.Sha512, 10),
            new HttpWantDigestPreference(HttpDigestAlgorithm.Sha256, 3));

        field.Serialize().ShouldBe("sha-512=10, sha-256=3");
    }

    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Want: Malformed preference values are rejected")]
    [InlineData("sha-256=:AAAA:")] // byte sequence, not an integer
    [InlineData("sha-256")] // bare key (boolean), not an integer
    [InlineData("")] // empty
    public void TryParse_Malformed_ReturnsFalse(string value)
    {
        HttpWantDigestField.TryParse(value, out HttpWantDigestField _, out string? error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }
}
