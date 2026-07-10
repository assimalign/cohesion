using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.DigestFields.Tests;

using Assimalign.Cohesion.Http;

public class HttpDigestHeaderRulesTests
{
    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Keys: Header keys emit their exact RFC 9530 field names")]
    [InlineData("Content-Digest")]
    [InlineData("Repr-Digest")]
    [InlineData("Want-Content-Digest")]
    [InlineData("Want-Repr-Digest")]
    public void HeaderKeys_MatchRfcFieldNames(string expected)
    {
        HttpHeaderKey key = expected switch
        {
            "Content-Digest" => HttpHeaderKey.ContentDigest,
            "Repr-Digest" => HttpHeaderKey.ReprDigest,
            "Want-Content-Digest" => HttpHeaderKey.WantContentDigest,
            _ => HttpHeaderKey.WantReprDigest,
        };

        key.Value.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Trailers: Digest fields are allowed in the trailer section")]
    public void DigestFields_AreNotProhibitedInTrailers()
    {
        // RFC 9530 §2.1 permits Content-Digest / Repr-Digest as trailers so a streamed body can be
        // hashed as it is written; HttpFieldRules must not classify them as trailer-prohibited.
        HttpFieldRules.IsProhibitedInTrailers(HttpHeaderKey.ContentDigest).ShouldBeFalse();
        HttpFieldRules.IsProhibitedInTrailers(HttpHeaderKey.ReprDigest).ShouldBeFalse();
    }
}
