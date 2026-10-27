using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Protocol.Tests;

public sealed class KeyValueFunctionNameTests
{
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair.Protocol] - Function names: shared ASCII identifiers are valid")]
    [InlineData("echo")]
    [InlineData("_function")]
    [InlineData("F1")]
    [InlineData("_")]
    public void IsValid_Identifier_ShouldAccept(string text)
    {
        KeyValueFunctionName.IsValid(text, out string? message).ShouldBeTrue();
        message.ShouldBeNull();
        KeyValueFunctionName name = text;
        name.Value.ShouldBe(text);
        name.ToString().ShouldBe(text);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair.Protocol] - Function names: invalid identifiers never enter CALL text")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1function")]
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    [InlineData("two names")]
    [InlineData("echo@p0")]
    [InlineData("echo;PUT")]
    [InlineData("écho")]
    [InlineData("echo\nPUT")]
    public void IsValid_InvalidIdentifier_ShouldReject(string text)
    {
        KeyValueFunctionName.IsValid(text, out string? message).ShouldBeFalse();
        message.ShouldNotBeNullOrEmpty();
        Should.Throw<ArgumentException>(() => new KeyValueFunctionName(text));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Protocol] - Function names: equality and hashes ignore case while spelling remains intact")]
    public void Equals_DifferentCase_ShouldUseOrdinalIgnoreCase()
    {
        KeyValueFunctionName left = "Echo";
        KeyValueFunctionName right = "eCHO";
        (left == right).ShouldBeTrue();
        left.GetHashCode().ShouldBe(right.GetHashCode());
        left.ToString().ShouldBe("Echo");
        default(KeyValueFunctionName).Value.ShouldBeNull();
    }
}
