using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace  Assimalign.Cohesion.ObjectValidation.Tests;

using  Assimalign.Cohesion.ObjectValidation;
using  Assimalign.Cohesion.ObjectValidation.Internal;

public class RuleEmailAddressTest
{
    public IValidationContext RunEmailAddressTest<TValue>(TValue value)
        where TValue : IEnumerable<char>
    {
        var rule = new EmailValidationRule<TValue>()
        {
            Error = new ValidationError()
            {

            }
        };

        if (rule.TryValidate((object)value, out var context))
        {
            return context;
        }
        else
        {
            throw new Exception("Unable to validate.");
        }
    }


    [Fact]
    public void EmailAddressSuccessTest()
    {
        var email = "ccrawford@assimalign.com";
        var context = this.RunEmailAddressTest(email);

        Assert.Empty(context.Errors);
    }


    [Fact]
    public void EmailAddressFailureTest01()
    {
        var email = "@ccrawford/assimalign.com";
        var context = this.RunEmailAddressTest(email);

        Assert.Single(context.Errors);
    }



    [Fact]
    public void EmailAddressFailureTest02()
    {
        var email = "ccrawford";
        var context = this.RunEmailAddressTest(email);

        Assert.Single(context.Errors);
    }

    // 'a@a.', 16,000 letters and a '!': the input #1377 measured at 27.9 s of CPU on the backtracking engine.
    private static readonly string QuadraticInput = "a@a." + new string('a', 16_000) + "!";

    // The rule's results before #1377, kept by the non-backtracking engine.
    [Theory(DisplayName = "Cohesion Test [ObjectValidation] - EmailAddress: an address keeps the result it had on the backtracking engine")]
    [InlineData("ccrawford@assimalign.com", true)]
    [InlineData("first.last@example.com", true)]
    [InlineData("user+tag@example.co.uk", true)]
    [InlineData("user@sub-domain.example.com", true)]
    [InlineData("\"quoted local\"@example.com", true)]
    [InlineData("\"a@b\"@example.com", true)]
    [InlineData("üser@exämple.com", true)]
    [InlineData("x@y.z9z", true)]
    [InlineData("@ccrawford/assimalign.com", false)]
    [InlineData("ccrawford", false)]
    [InlineData("first..last@example.com", false)]
    [InlineData(".first@example.com", false)]
    [InlineData("user@-example.com", false)]
    [InlineData("user@example.com.", false)]
    [InlineData("user@@example.com", false)]
    [InlineData("user@[127.0.0.1]", false)]
    [InlineData("x@y.z9", false)]
    [InlineData("a@b", false)]
    public void TryValidate_KnownAddress_ShouldKeepItsResult(string address, bool valid)
    {
        // Act
        var context = this.RunEmailAddressTest(address);

        // Assert
        context.Errors.Count().ShouldBe(valid ? 0 : 1);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - EmailAddress: a 16,000-character input fails in well under 100 ms")]
    public void TryValidate_QuadraticInputOf16000Characters_ShouldFailInUnder100Milliseconds()
    {
        // Arrange: the first validation builds the pattern; the timing is of a validation after that.
        this.RunEmailAddressTest("ccrawford@assimalign.com");

        // Act
        long started = Stopwatch.GetTimestamp();
        var context = this.RunEmailAddressTest(QuadraticInput);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        // Assert
        context.Errors.Count().ShouldBe(1);
        elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - EmailAddress: the pattern matches a 16,000-character input in linear time, without the length cap")]
    public void Syntax_QuadraticInputOf16000Characters_ShouldMatchInUnder100Milliseconds()
    {
        // Arrange: the length cap rejects this input before the pattern sees it, so the pattern is timed directly.
        EmailAddressFormat.Syntax.IsMatch("ccrawford@assimalign.com");

        // Act
        long started = Stopwatch.GetTimestamp();
        bool matched = EmailAddressFormat.Syntax.IsMatch(QuadraticInput);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        // Assert
        matched.ShouldBeFalse();
        elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - EmailAddress: a 64-octet local part passes and a 65-octet one fails")]
    public void TryValidate_LocalPartOver64Octets_ShouldFail()
    {
        // Arrange
        string atLimit = new string('a', 64) + "@example.com";
        string overLimit = new string('a', 65) + "@example.com";

        // Act
        var atLimitContext = this.RunEmailAddressTest(atLimit);
        var overLimitContext = this.RunEmailAddressTest(overLimit);

        // Assert
        atLimitContext.Errors.ShouldBeEmpty();
        overLimitContext.Errors.Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - EmailAddress: the local part limit counts UTF-8 octets, not characters")]
    public void TryValidate_LocalPartOf33TwoOctetCharacters_ShouldFail()
    {
        // Arrange: 33 characters, 66 octets.
        string address = new string('ü', 33) + "@example.com";

        // Act
        var context = this.RunEmailAddressTest(address);

        // Assert
        context.Errors.Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - EmailAddress: a 254-octet address passes and a 255-octet one fails")]
    public void TryValidate_AddressOver254Octets_ShouldFail()
    {
        // Arrange: a 64-octet local part, '@', two 63-octet labels, their dots and '.com' make 197 octets.
        string prefix = new string('a', 64) + "@" + new string('b', 63) + "." + new string('c', 63) + ".";
        string atLimit = prefix + new string('d', 57) + ".com";
        string overLimit = prefix + new string('d', 58) + ".com";

        // Act
        var atLimitContext = this.RunEmailAddressTest(atLimit);
        var overLimitContext = this.RunEmailAddressTest(overLimit);

        // Assert
        atLimit.Length.ShouldBe(254);
        atLimitContext.Errors.ShouldBeEmpty();
        overLimitContext.Errors.Count().ShouldBe(1);
    }
}