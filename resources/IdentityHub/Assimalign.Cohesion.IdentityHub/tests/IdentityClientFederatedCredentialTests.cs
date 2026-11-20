using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A federated credential names the token of another issuer that stands for the application.
/// </summary>
public class IdentityClientFederatedCredentialTests
{
    private const string validIssuer = "https://token.actions.example";
    private const string validSubject = "repo:assimalign/orders:ref:refs/heads/main";

    private static readonly DateTimeOffset _now = TestClients.Now;

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should keep the issuer, the subject, and the audiences exactly")]
    public void Create_WithAnIssuerSubjectAndAudiences_ShouldKeepEachExactly()
    {
        // Act
        IdentityClientFederatedCredential credential = IdentityClientFederatedCredential.Create(
            "build",
            validIssuer,
            validSubject,
            ["api://token-exchange", "orders"],
            "Build pipeline",
            _now,
            _now.AddDays(90));

        // Assert
        credential.Id.Value.ShouldBe("build");
        credential.Kind.ShouldBe(IdentityCredentialKind.FederatedToken);
        credential.Issuer.ShouldBe(validIssuer);
        credential.Subject.ShouldBe(validSubject);
        credential.Audiences.Select(audience => audience.Value).ShouldBe(["api://token-exchange", "orders"]);
        credential.DisplayName.ShouldBe("Build pipeline");
        credential.NotBefore.ShouldBe(_now);
        credential.ExpiresAt.ShouldBe(_now.AddDays(90));
        credential.ToString().ShouldBe("build (FederatedToken)");
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse an issuer that is not an https URL without query or fragment")]
    [InlineData("")]
    [InlineData("http://token.actions.example")]
    [InlineData("token.actions.example")]
    [InlineData("https://token.actions.example?tenant=a")]
    [InlineData("https://token.actions.example#fragment")]
    [InlineData("https://user@token.actions.example")]
    [InlineData("https://token.actions example")]
    [InlineData("urn:issuer")]
    public void Create_WithAnIssuerOutsideItsRule_ShouldThrowArgumentException(string issuer)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClientFederatedCredential.Create("build", issuer, validSubject, ["orders"]));

        // Assert
        exception.ParamName.ShouldBe("issuer");
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a blank subject and one with a control character")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("repo:orders\nref:main")]
    public void Create_WithASubjectOutsideItsRule_ShouldThrowArgumentException(string subject)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClientFederatedCredential.Create("build", validIssuer, subject, ["orders"]));

        // Assert
        exception.ParamName.ShouldBe("subject");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse no audience, too many, a default one, and a repeated one")]
    public void Create_WithAudiencesOutsideTheirRule_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityClientAudience[] tooMany = Enumerable
            .Range(0, IdentityClientFederatedCredential.MaxAudienceCount + 1)
            .Select(index => new IdentityClientAudience($"audience-{index}"))
            .ToArray();
        IdentityClientAudience[] most = tooMany[..IdentityClientFederatedCredential.MaxAudienceCount];

        // Act
        IdentityClientFederatedCredential atLimit = IdentityClientFederatedCredential.Create("build", validIssuer, validSubject, most);
        ArgumentException none = Should.Throw<ArgumentException>(
            () => IdentityClientFederatedCredential.Create("build", validIssuer, validSubject, []));
        ArgumentException beyond = Should.Throw<ArgumentException>(
            () => IdentityClientFederatedCredential.Create("build", validIssuer, validSubject, tooMany));
        ArgumentException missing = Should.Throw<ArgumentException>(
            () => IdentityClientFederatedCredential.Create("build", validIssuer, validSubject, [default]));
        ArgumentException repeated = Should.Throw<ArgumentException>(
            () => IdentityClientFederatedCredential.Create("build", validIssuer, validSubject, ["orders", "orders"]));

        // Assert
        IdentityClientFederatedCredential.MaxAudienceCount.ShouldBe(8);
        atLimit.Audiences.Count.ShouldBe(8);
        none.ParamName.ShouldBe("audiences");
        beyond.ParamName.ShouldBe("audiences");
        missing.ParamName.ShouldBe("audiences");
        repeated.ParamName.ShouldBe("audiences");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null issuer, subject, or audience list")]
    public void Create_WithANullArgument_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException issuer = Should.Throw<ArgumentNullException>(
            () => IdentityClientFederatedCredential.Create("build", null!, validSubject, ["orders"]));
        ArgumentNullException subject = Should.Throw<ArgumentNullException>(
            () => IdentityClientFederatedCredential.Create("build", validIssuer, null!, ["orders"]));
        ArgumentNullException audiences = Should.Throw<ArgumentNullException>(
            () => IdentityClientFederatedCredential.Create("build", validIssuer, validSubject, null!));

        // Assert
        issuer.ParamName.ShouldBe("issuer");
        subject.ParamName.ShouldBe("subject");
        audiences.ParamName.ShouldBe("audiences");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should copy the audiences")]
    public void Create_ThenChangingTheSource_ShouldNotChangeTheCredential()
    {
        // Arrange
        var audiences = new List<IdentityClientAudience> { "orders" };
        IdentityClientFederatedCredential credential = IdentityClientFederatedCredential.Create("build", validIssuer, validSubject, audiences);

        // Act
        audiences.Add("billing");

        // Assert
        credential.Audiences.Count.ShouldBe(1);
    }
}
