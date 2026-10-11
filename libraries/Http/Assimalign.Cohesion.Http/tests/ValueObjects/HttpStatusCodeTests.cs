using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpStatusCodeTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - HttpStatusCode.ToString: renders the code and its reason phrase")]
    [InlineData(200, "200 OK")]
    [InlineData(300, "300 Multiple Choices")]
    [InlineData(425, "425 Too Early")]
    public void ToString_KnownStatusCode_ShouldReturnCodeAndReasonPhrase(int value, string expected)
    {
        // Arrange
        HttpStatusCode statusCode = value;

        // Act
        string actual = statusCode.ToString();

        // Assert
        actual.ShouldBe(expected);
    }

    // The IANA HTTP Status Code Registry's assigned codes and phrases: RFC 9110 §15 for the codes it
    // defines, the defining specification for the rest (#1204). The reserved 418 and the registry's
    // temporary registrations are left out; the reserved 306 stays only because the obsolete
    // HttpStatusCode.Unused shipped in 10.0.0-preview.1.
    [Theory(DisplayName = "Cohesion Test [Http] - HttpStatusCode.ReasonPhrase: every registered code carries the registry's phrase")]
    [InlineData(100, "Continue")]
    [InlineData(101, "Switching Protocols")]
    [InlineData(102, "Processing")]
    [InlineData(103, "Early Hints")]
    [InlineData(200, "OK")]
    [InlineData(201, "Created")]
    [InlineData(202, "Accepted")]
    [InlineData(203, "Non-Authoritative Information")]
    [InlineData(204, "No Content")]
    [InlineData(205, "Reset Content")]
    [InlineData(206, "Partial Content")]
    [InlineData(207, "Multi-Status")]
    [InlineData(208, "Already Reported")]
    [InlineData(226, "IM Used")]
    [InlineData(300, "Multiple Choices")]
    [InlineData(301, "Moved Permanently")]
    [InlineData(302, "Found")]
    [InlineData(303, "See Other")]
    [InlineData(304, "Not Modified")]
    [InlineData(305, "Use Proxy")]
    [InlineData(306, "(Unused)")]
    [InlineData(307, "Temporary Redirect")]
    [InlineData(308, "Permanent Redirect")]
    [InlineData(400, "Bad Request")]
    [InlineData(401, "Unauthorized")]
    [InlineData(402, "Payment Required")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "Not Found")]
    [InlineData(405, "Method Not Allowed")]
    [InlineData(406, "Not Acceptable")]
    [InlineData(407, "Proxy Authentication Required")]
    [InlineData(408, "Request Timeout")]
    [InlineData(409, "Conflict")]
    [InlineData(410, "Gone")]
    [InlineData(411, "Length Required")]
    [InlineData(412, "Precondition Failed")]
    [InlineData(413, "Content Too Large")]
    [InlineData(414, "URI Too Long")]
    [InlineData(415, "Unsupported Media Type")]
    [InlineData(416, "Range Not Satisfiable")]
    [InlineData(417, "Expectation Failed")]
    [InlineData(421, "Misdirected Request")]
    [InlineData(422, "Unprocessable Content")]
    [InlineData(423, "Locked")]
    [InlineData(424, "Failed Dependency")]
    [InlineData(425, "Too Early")]
    [InlineData(426, "Upgrade Required")]
    [InlineData(428, "Precondition Required")]
    [InlineData(429, "Too Many Requests")]
    [InlineData(431, "Request Header Fields Too Large")]
    [InlineData(451, "Unavailable For Legal Reasons")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(501, "Not Implemented")]
    [InlineData(502, "Bad Gateway")]
    [InlineData(503, "Service Unavailable")]
    [InlineData(504, "Gateway Timeout")]
    [InlineData(505, "HTTP Version Not Supported")]
    [InlineData(506, "Variant Also Negotiates")]
    [InlineData(507, "Insufficient Storage")]
    [InlineData(508, "Loop Detected")]
    [InlineData(510, "Not Extended")] // the registry's "(OBSOLETED)" annotation is not part of the phrase
    [InlineData(511, "Network Authentication Required")]
    public void ReasonPhrase_RegisteredStatusCode_ShouldBeRegistryPhrase(int value, string expected)
    {
        // Arrange
        HttpStatusCode statusCode = value;

        // Act
        string reasonPhrase = statusCode.ReasonPhrase;
        string rendered = statusCode.ToString();

        // Assert
        reasonPhrase.ShouldBe(expected);
        rendered.ShouldBe(value + " " + expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpStatusCode.ReasonPhrase: a default instance has no phrase")]
    public void ReasonPhrase_DefaultInstance_ShouldBeEmpty()
    {
        // Arrange
        HttpStatusCode statusCode = default;

        // Act
        string reasonPhrase = statusCode.ReasonPhrase;
        string rendered = statusCode.ToString();

        // Assert
        reasonPhrase.ShouldBeEmpty();
        rendered.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpStatusCode.ImUsed: is 226 IM Used")]
    public void ImUsed_ShouldBe226()
    {
        // Act
        HttpStatusCode statusCode = HttpStatusCode.ImUsed;

        // Assert
        statusCode.Value.ShouldBe(226);
        statusCode.ToString().ShouldBe("226 IM Used");
    }

    // 226 is registered (RFC 3229). 299 and the reserved 418 are three-digit codes RFC 9110 §15 lets a
    // sender use, but only assigned codes are known here: the documented limit of IsValid. The reserved
    // 306 is known only because the obsolete HttpStatusCode.Unused shipped in 10.0.0-preview.1, and
    // dropping it would make that accessor throw.
    [Theory(DisplayName = "Cohesion Test [Http] - HttpStatusCode.IsValid: accepts registered codes only")]
    [InlineData(200, true)]
    [InlineData(226, true)]
    [InlineData(300, true)]
    [InlineData(306, true)]
    [InlineData(425, true)]
    [InlineData(299, false)]
    [InlineData(418, false)]
    [InlineData(999, false)]
    public void IsValid_KnownAndUnknownCodes_ShouldReturnExpectedResult(int value, bool expected)
    {
        // Act
        bool actual = HttpStatusCode.IsValid(value);

        // Assert
        actual.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpStatusCode: implicit conversions round-trip the numeric value")]
    public void ImplicitConversions_ShouldRoundTripNumericValue()
    {
        // Arrange
        HttpStatusCode statusCode = HttpStatusCode.NoContent;

        // Act
        int numericValue = statusCode;

        // Assert
        numericValue.ShouldBe(204);
        ((HttpStatusCode)numericValue).ShouldBe(HttpStatusCode.NoContent);
    }
}
