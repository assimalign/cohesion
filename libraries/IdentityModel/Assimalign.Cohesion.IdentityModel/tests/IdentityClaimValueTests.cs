using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the typed claim value union.
/// </summary>
public sealed class IdentityClaimValueTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Factories should produce the declared kinds")]
    public void Factories_WhenInvoked_ShouldProduceTheDeclaredKinds()
    {
        IdentityClaimValue.FromString("a").Kind.ShouldBe(IdentityValueKind.String);
        IdentityClaimValue.FromBoolean(true).Kind.ShouldBe(IdentityValueKind.Boolean);
        IdentityClaimValue.FromInteger(42).Kind.ShouldBe(IdentityValueKind.Integer);
        IdentityClaimValue.FromDouble(1.5).Kind.ShouldBe(IdentityValueKind.Double);
        IdentityClaimValue.FromDecimal(1.5m).Kind.ShouldBe(IdentityValueKind.Decimal);
        IdentityClaimValue.FromDateTime(DateTimeOffset.UnixEpoch).Kind.ShouldBe(IdentityValueKind.DateTime);
        IdentityClaimValue.FromBinary(new byte[] { 1, 2 }).Kind.ShouldBe(IdentityValueKind.Binary);
        IdentityClaimValue.FromArray([IdentityClaimValue.FromInteger(1)]).Kind.ShouldBe(IdentityValueKind.Array);
        IdentityClaimValue.FromObject([new("a", IdentityClaimValue.FromInteger(1))]).Kind.ShouldBe(IdentityValueKind.Object);
        IdentityClaimValue.Null.Kind.ShouldBe(IdentityValueKind.Null);
        IdentityClaimValue.Null.IsNull.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Accessors should round-trip content")]
    public void Accessors_WhenKindMatches_ShouldRoundTripContent()
    {
        var timestamp = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.FromHours(2));

        IdentityClaimValue.FromString("hello").AsString().ShouldBe("hello");
        IdentityClaimValue.FromBoolean(true).AsBoolean().ShouldBeTrue();
        IdentityClaimValue.FromInteger(long.MinValue).AsInteger().ShouldBe(long.MinValue);
        IdentityClaimValue.FromDouble(1e300).AsDouble().ShouldBe(1e300);
        IdentityClaimValue.FromDecimal(79.228m).AsDecimal().ShouldBe(79.228m);
        IdentityClaimValue.FromDateTime(timestamp).AsDateTime().ShouldBe(timestamp);
        IdentityClaimValue.FromDateTime(timestamp).AsDateTime().Offset.ShouldBe(TimeSpan.FromHours(2));
        IdentityClaimValue.FromBinary(new byte[] { 1, 2, 3 }).AsBinary().ToArray().ShouldBe(new byte[] { 1, 2, 3 });

        var array = IdentityClaimValue.FromArray([IdentityClaimValue.FromInteger(1), IdentityClaimValue.FromString("x")]);
        array.AsArray().Count.ShouldBe(2);
        array.AsArray()[1].AsString().ShouldBe("x");

        var composite = IdentityClaimValue.FromObject([new("inner", IdentityClaimValue.FromBoolean(false))]);
        composite.AsObject()["inner"].AsBoolean().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Accessor kind mismatch should throw IdentityModelException")]
    public void Accessors_WhenKindMismatches_ShouldThrowIdentityModelException()
    {
        var value = IdentityClaimValue.FromString("not a number");

        Should.Throw<IdentityModelException>(() => value.AsInteger());
        Should.Throw<IdentityModelException>(() => value.AsBoolean());
        Should.Throw<IdentityModelException>(() => value.AsArray());

        value.TryGetInteger(out _).ShouldBeFalse();
        value.TryGetString(out var text).ShouldBeTrue();
        text.ShouldBe("not a number");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Default instance should be inert and undefined")]
    public void DefaultInstance_WhenInspected_ShouldBeInertAndUndefined()
    {
        var value = default(IdentityClaimValue);

        value.Kind.ShouldBe(IdentityValueKind.Undefined);
        value.IsUndefined.ShouldBeTrue();
        value.IsNull.ShouldBeFalse();
        value.ToString().ShouldBe(string.Empty);
        value.TryGetString(out _).ShouldBeFalse();
        Should.Throw<IdentityModelException>(() => value.AsString());
        (value == default).ShouldBeTrue();
        (value == IdentityClaimValue.Null).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Equality should be structural and kind-sensitive")]
    public void Equality_WhenCompared_ShouldBeStructuralAndKindSensitive()
    {
        // Same content, same kind.
        (IdentityClaimValue.FromString("a") == IdentityClaimValue.FromString("a")).ShouldBeTrue();
        (IdentityClaimValue.FromInteger(1) == IdentityClaimValue.FromInteger(1)).ShouldBeTrue();

        // Same numeral, different kinds — never equal.
        (IdentityClaimValue.FromInteger(1) == IdentityClaimValue.FromDouble(1)).ShouldBeFalse();
        (IdentityClaimValue.FromDouble(1) == IdentityClaimValue.FromDecimal(1)).ShouldBeFalse();

        // Binary compares by content.
        (IdentityClaimValue.FromBinary(new byte[] { 1, 2 }) == IdentityClaimValue.FromBinary(new byte[] { 1, 2 })).ShouldBeTrue();
        (IdentityClaimValue.FromBinary(new byte[] { 1, 2 }) == IdentityClaimValue.FromBinary(new byte[] { 2, 1 })).ShouldBeFalse();

        // DateTime compares instant AND offset.
        var utc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var shifted = utc.ToOffset(TimeSpan.FromHours(1));
        (IdentityClaimValue.FromDateTime(utc) == IdentityClaimValue.FromDateTime(shifted)).ShouldBeFalse();

        // Double compares by bit pattern: NaN equals NaN.
        (IdentityClaimValue.FromDouble(double.NaN) == IdentityClaimValue.FromDouble(double.NaN)).ShouldBeTrue();

        // Array order is significant.
        var oneTwo = IdentityClaimValue.FromArray([IdentityClaimValue.FromInteger(1), IdentityClaimValue.FromInteger(2)]);
        var twoOne = IdentityClaimValue.FromArray([IdentityClaimValue.FromInteger(2), IdentityClaimValue.FromInteger(1)]);
        (oneTwo == twoOne).ShouldBeFalse();
        (oneTwo == IdentityClaimValue.FromArray([IdentityClaimValue.FromInteger(1), IdentityClaimValue.FromInteger(2)])).ShouldBeTrue();

        // Object member order is insignificant.
        var forward = IdentityClaimValue.FromObject([new("a", IdentityClaimValue.FromInteger(1)), new("b", IdentityClaimValue.FromInteger(2))]);
        var backward = IdentityClaimValue.FromObject([new("b", IdentityClaimValue.FromInteger(2)), new("a", IdentityClaimValue.FromInteger(1))]);
        (forward == backward).ShouldBeTrue();
        forward.GetHashCode().ShouldBe(backward.GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Factories should snapshot caller content")]
    public void Factories_WhenSourceMutatesAfterConstruction_ShouldNotChange()
    {
        // Arrange
        var bytes = new byte[] { 1, 2, 3 };
        var elements = new List<IdentityClaimValue> { IdentityClaimValue.FromInteger(1) };
        var members = new Dictionary<string, IdentityClaimValue> { ["a"] = IdentityClaimValue.FromInteger(1) };

        var binary = IdentityClaimValue.FromBinary(bytes);
        var array = IdentityClaimValue.FromArray(elements);
        var composite = IdentityClaimValue.FromObject(members);

        // Act — mutate every source after construction.
        bytes[0] = 99;
        elements.Add(IdentityClaimValue.FromInteger(2));
        members["b"] = IdentityClaimValue.FromInteger(2);

        // Assert — snapshots are unaffected.
        binary.AsBinary().ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        array.AsArray().Count.ShouldBe(1);
        composite.AsObject().Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Undefined elements should be rejected")]
    public void Factories_WhenGivenUndefinedElements_ShouldThrow()
    {
        Should.Throw<ArgumentException>(() => IdentityClaimValue.FromArray([default]));
        Should.Throw<ArgumentException>(() => IdentityClaimValue.FromObject([new("a", default)]));
        Should.Throw<ArgumentException>(() => IdentityClaimValue.FromObject(
            [new("a", IdentityClaimValue.Null), new("a", IdentityClaimValue.Null)]));
        Should.Throw<ArgumentNullException>(() => IdentityClaimValue.FromString(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Nesting beyond MaxDepth should throw IdentityModelException")]
    public void FromArray_WhenNestedBeyondMaxDepth_ShouldThrowIdentityModelException()
    {
        // Arrange — build a chain exactly at the limit, then push one past it.
        var value = IdentityClaimValue.FromInteger(0);
        for (var depth = 0; depth < IdentityClaimValue.MaxDepth; depth++)
        {
            value = IdentityClaimValue.FromArray([value]);
        }

        // Act + Assert
        var deepest = value;
        Should.Throw<IdentityModelException>(() => IdentityClaimValue.FromArray([deepest]));
        Should.Throw<IdentityModelException>(() => IdentityClaimValue.FromObject([new("a", deepest)]));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: ToString should render culture-invariantly")]
    public void ToString_WhenRendered_ShouldBeCultureInvariant()
    {
        IdentityClaimValue.Null.ToString().ShouldBe("null");
        IdentityClaimValue.FromBoolean(true).ToString().ShouldBe("true");
        IdentityClaimValue.FromInteger(-42).ToString().ShouldBe("-42");
        IdentityClaimValue.FromDouble(1.5).ToString().ShouldBe("1.5");
        IdentityClaimValue.FromDecimal(1.50m).ToString().ShouldBe("1.50");
        IdentityClaimValue.FromBinary(new byte[] { 1, 2 }).ToString().ShouldBe(Convert.ToBase64String(new byte[] { 1, 2 }));
        IdentityClaimValue.FromArray([IdentityClaimValue.FromInteger(1), IdentityClaimValue.FromString("x")])
            .ToString().ShouldBe("[1, x]");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Implicit conversions should map to the natural kinds")]
    public void ImplicitConversions_WhenUsed_ShouldMapToTheNaturalKinds()
    {
        IdentityClaimValue fromString = "text";
        IdentityClaimValue fromNullString = (string?)null;
        IdentityClaimValue fromBool = true;
        IdentityClaimValue fromLong = 42L;
        IdentityClaimValue fromDouble = 1.5;
        IdentityClaimValue fromDecimal = 1.5m;
        IdentityClaimValue fromDate = DateTimeOffset.UnixEpoch;

        fromString.Kind.ShouldBe(IdentityValueKind.String);
        fromNullString.Kind.ShouldBe(IdentityValueKind.Null);
        fromBool.Kind.ShouldBe(IdentityValueKind.Boolean);
        fromLong.Kind.ShouldBe(IdentityValueKind.Integer);
        fromDouble.Kind.ShouldBe(IdentityValueKind.Double);
        fromDecimal.Kind.ShouldBe(IdentityValueKind.Decimal);
        fromDate.Kind.ShouldBe(IdentityValueKind.DateTime);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimValue: Kind ordinals should be stable")]
    public void IdentityValueKind_WhenInspected_ShouldHaveStableOrdinals()
    {
        ((int)IdentityValueKind.Undefined).ShouldBe(0);
        ((int)IdentityValueKind.Null).ShouldBe(1);
        ((int)IdentityValueKind.String).ShouldBe(2);
        ((int)IdentityValueKind.Boolean).ShouldBe(3);
        ((int)IdentityValueKind.Integer).ShouldBe(4);
        ((int)IdentityValueKind.Double).ShouldBe(5);
        ((int)IdentityValueKind.Decimal).ShouldBe(6);
        ((int)IdentityValueKind.DateTime).ShouldBe(7);
        ((int)IdentityValueKind.Binary).ShouldBe(8);
        ((int)IdentityValueKind.Array).ShouldBe(9);
        ((int)IdentityValueKind.Object).ShouldBe(10);
    }
}
