using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ProblemDetailsTests;

public class ProblemDetailsValidationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.ProblemDetails] - Status: invalid codes should fail when assigned")]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(600)]
    [InlineData(int.MaxValue)]
    public void Status_InvalidCode_ShouldThrowBeforeSerialization(int status)
    {
        // Arrange
        ProblemDetails problem = new() { Status = 400 };

        // Act
        Action act = () => problem.Status = status;

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>().ParamName.ShouldBe("value");
        problem.Status.ShouldBe(400);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ProblemDetails] - Status: null and all HTTP status classes remain assignable")]
    [InlineData(null)]
    [InlineData(100)]
    [InlineData(199)]
    [InlineData(299)]
    [InlineData(399)]
    [InlineData(499)]
    [InlineData(599)]
    public void Status_ValidCode_ShouldRetainValue(int? status)
    {
        // Arrange
        ProblemDetails problem = new();

        // Act
        problem.Status = status;

        // Assert
        problem.Status.ShouldBe(status);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ProblemDetails] - FromStatus: an uninitialized status should fail at construction")]
    public void FromStatus_DefaultStatus_ShouldThrowBeforeSerialization()
    {
        // Arrange
        HttpStatusCode status = default;

        // Act
        Action act = () => ProblemDetails.FromStatus(status);

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.ProblemDetails] - Type: malformed URI references should fail when assigned")]
    [InlineData("not a uri")]
    [InlineData("https://[broken")]
    [InlineData("//[broken")]
    [InlineData("//[v1.]/path")]
    [InlineData("//[fe80::1%25eth0]/path")]
    [InlineData("//[::ffff:192.168.1.01]/path")]
    [InlineData("//user@@host/path")]
    [InlineData("//[::1]extra/path")]
    [InlineData("//host:80:90/path")]
    [InlineData("//example.com:bad/problem")]
    [InlineData("https://example.com/%zz")]
    [InlineData("/%")]
    [InlineData("bad%zz/../valid")]
    [InlineData("[bad]/../valid")]
    [InlineData("/problem%2")]
    [InlineData("1invalid:problem")]
    [InlineData("/problem\\name")]
    [InlineData("/problem\r\nname")]
    [InlineData("https://example.com/caf\u00e9")]
    [InlineData("#first#second")]
    public void Type_InvalidUriReference_ShouldThrowBeforeSerialization(string reference)
    {
        // Arrange
        ProblemDetails problem = new() { Type = "about:blank" };

        // Act
        Action act = () => problem.Type = reference;

        // Assert
        act.ShouldThrow<ArgumentException>().ParamName.ShouldBe("value");
        problem.Type.ShouldBe("about:blank");
    }

    [Theory(DisplayName = "Cohesion Test [Web.ProblemDetails] - Instance: malformed URI references should fail when assigned")]
    [InlineData("not a uri")]
    [InlineData("https://[broken")]
    [InlineData("//[broken")]
    [InlineData("//[v1.]/path")]
    [InlineData("//[fe80::1%25eth0]/path")]
    [InlineData("//[::ffff:192.168.1.01]/path")]
    [InlineData("//user@@host/path")]
    [InlineData("//[::1]extra/path")]
    [InlineData("//host:80:90/path")]
    [InlineData("//example.com:bad/problem")]
    [InlineData("https://example.com/%zz")]
    [InlineData("/%")]
    [InlineData("bad%zz/../valid")]
    [InlineData("[bad]/../valid")]
    [InlineData("/problem%2")]
    [InlineData("1invalid:problem")]
    [InlineData("/problem\\name")]
    [InlineData("/problem\r\nname")]
    [InlineData("https://example.com/caf\u00e9")]
    [InlineData("#first#second")]
    public void Instance_InvalidUriReference_ShouldThrowBeforeSerialization(string reference)
    {
        // Arrange
        ProblemDetails problem = new() { Instance = "/occurrence/1" };

        // Act
        Action act = () => problem.Instance = reference;

        // Assert
        act.ShouldThrow<ArgumentException>().ParamName.ShouldBe("value");
        problem.Instance.ShouldBe("/occurrence/1");
    }

    [Theory(DisplayName = "Cohesion Test [Web.ProblemDetails] - URI members: valid URI references should preserve their original spelling")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    [InlineData("urn:example:problem")]
    [InlineData("x:problem")]
    [InlineData("x:")]
    [InlineData("https://example.com/problems/missing")]
    [InlineData("//example.com/problems/missing")]
    [InlineData("//a!b/path")]
    [InlineData("//a%20b/path")]
    [InlineData("//[v1.a]/path")]
    [InlineData("//example.com:65536/path")]
    [InlineData("//:80/path")]
    [InlineData("//")]
    [InlineData("///path")]
    [InlineData("scheme://a!b:999999/path")]
    [InlineData("scheme://user:password@a%20b/path")]
    [InlineData("scheme://[::1]/path")]
    [InlineData("scheme://[::ffff:192.0.2.1]/path")]
    [InlineData("/problems/missing")]
    [InlineData("../problems/missing")]
    [InlineData("./problem:name")]
    [InlineData("?problem=missing")]
    [InlineData("?")]
    [InlineData("#missing")]
    [InlineData("#")]
    [InlineData("/problem?name=missing#details")]
    [InlineData("/problem%20name")]
    [InlineData("/caf%C3%A9")]
    public void UriMembers_ValidReference_ShouldPreserveOriginalValue(string? reference)
    {
        // Arrange
        ProblemDetails problem = new();

        // Act
        problem.Type = reference;
        problem.Instance = reference;

        // Assert
        problem.Type.ShouldBe(reference);
        problem.Instance.ShouldBe(reference);
    }
}
