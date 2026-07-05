using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9651 &#167; 3.1 / &#167; 3.2 / &#167; 4 conformance tests for the three top-level structured field
/// types (Item, List, Dictionary), parameters, inner lists, multi-line combining, strict
/// fail-parsing, and canonical serialization round-trips.
/// </summary>
public class StructuredFieldTests
{
    // ============================================================================
    // List (§3.1 / §4.2.1)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - SFV List: token members parse and round-trip")]
    public void List_TokenMembers_RoundTrip()
    {
        StructuredFieldList.TryParse("sugar, tea, rum", out StructuredFieldList list, out string? error).ShouldBeTrue(error);

        list.Count.ShouldBe(3);
        list[0].IsInnerList.ShouldBeFalse();
        list[0].Item.Value.AsToken().ShouldBe("sugar");
        list[2].Item.Value.AsToken().ShouldBe("rum");
        list.Serialize().ShouldBe("sugar, tea, rum");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV List: empty input is an empty list")]
    public void List_EmptyInput_EmptyList()
    {
        StructuredFieldList.TryParse("", out StructuredFieldList list, out _).ShouldBeTrue();

        list.Count.ShouldBe(0);
        list.Serialize().ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV List: members carry parameters")]
    public void List_MembersWithParameters_Parse()
    {
        StructuredFieldList.TryParse("abc;a=1;b=2, def", out StructuredFieldList list, out string? error).ShouldBeTrue(error);

        list.Count.ShouldBe(2);
        StructuredFieldParameters parameters = list[0].Item.Parameters;
        parameters.Count.ShouldBe(2);
        parameters.TryGetValue("a", out StructuredFieldBareItem a).ShouldBeTrue();
        a.AsInteger().ShouldBe(1);
        list.Serialize().ShouldBe("abc;a=1;b=2, def");
    }

    // ============================================================================
    // Inner lists (§3.1.1 / §4.2.1.2)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - SFV InnerList: parses items and round-trips")]
    public void InnerList_ItemsAndParameters_RoundTrip()
    {
        StructuredFieldList.TryParse("(1 2 3), (a b)", out StructuredFieldList list, out string? error).ShouldBeTrue(error);

        list.Count.ShouldBe(2);
        list[0].IsInnerList.ShouldBeTrue();
        StructuredFieldInnerList inner = list[0].InnerList;
        inner.Count.ShouldBe(3);
        inner[0].Value.AsInteger().ShouldBe(1);
        list.Serialize().ShouldBe("(1 2 3), (a b)");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV InnerList: empty inner list with parameters round-trips")]
    public void InnerList_EmptyWithParameters_RoundTrip()
    {
        StructuredFieldList.TryParse("();a=1", out StructuredFieldList list, out string? error).ShouldBeTrue(error);

        list.Count.ShouldBe(1);
        list[0].IsInnerList.ShouldBeTrue();
        list[0].InnerList.Count.ShouldBe(0);
        list[0].InnerList.Parameters.Count.ShouldBe(1);
        list.Serialize().ShouldBe("();a=1");
    }

    // ============================================================================
    // Dictionary (§3.2 / §4.2.2)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Dictionary: key/value members parse and round-trip")]
    public void Dictionary_Members_RoundTrip()
    {
        StructuredFieldDictionary.TryParse("a=1, b=2, c=3", out StructuredFieldDictionary dict, out string? error).ShouldBeTrue(error);

        dict.Count.ShouldBe(3);
        dict.TryGetValue("b", out StructuredFieldMember b).ShouldBeTrue();
        b.Item.Value.AsInteger().ShouldBe(2);
        dict.Serialize().ShouldBe("a=1, b=2, c=3");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Dictionary: boolean-true members use the bare-key short form")]
    public void Dictionary_BooleanTrueMembers_ShortForm()
    {
        StructuredFieldDictionary.TryParse("a, b=?0, c", out StructuredFieldDictionary dict, out string? error).ShouldBeTrue(error);

        dict.Count.ShouldBe(3);
        dict.TryGetValue("a", out StructuredFieldMember a).ShouldBeTrue();
        a.Item.Value.Type.ShouldBe(StructuredFieldType.Boolean);
        a.Item.Value.AsBoolean().ShouldBeTrue();
        dict.TryGetValue("b", out StructuredFieldMember b).ShouldBeTrue();
        b.Item.Value.AsBoolean().ShouldBeFalse();
        dict.Serialize().ShouldBe("a, b=?0, c");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Dictionary: bare key retains parameters")]
    public void Dictionary_BareKeyWithParameters_Parse()
    {
        StructuredFieldDictionary.TryParse("a;x=1", out StructuredFieldDictionary dict, out string? error).ShouldBeTrue(error);

        dict.TryGetValue("a", out StructuredFieldMember a).ShouldBeTrue();
        a.Item.Value.AsBoolean().ShouldBeTrue();
        a.Parameters.TryGetValue("x", out StructuredFieldBareItem x).ShouldBeTrue();
        x.AsInteger().ShouldBe(1);
        dict.Serialize().ShouldBe("a;x=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Dictionary: inner-list values parse and round-trip")]
    public void Dictionary_InnerListValues_RoundTrip()
    {
        StructuredFieldDictionary.TryParse("rating=1.5, colors=(red green blue)", out StructuredFieldDictionary dict, out string? error).ShouldBeTrue(error);

        dict.TryGetValue("colors", out StructuredFieldMember colors).ShouldBeTrue();
        colors.IsInnerList.ShouldBeTrue();
        colors.InnerList.Count.ShouldBe(3);
        dict.Serialize().ShouldBe("rating=1.5, colors=(red green blue)");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Dictionary: duplicate key keeps the last value")]
    public void Dictionary_DuplicateKey_LastWins()
    {
        StructuredFieldDictionary.TryParse("a=1, b=2, a=3", out StructuredFieldDictionary dict, out string? error).ShouldBeTrue(error);

        dict.Count.ShouldBe(2);
        dict.TryGetValue("a", out StructuredFieldMember a).ShouldBeTrue();
        a.Item.Value.AsInteger().ShouldBe(3);
        dict.Serialize().ShouldBe("a=3, b=2");
    }

    // ============================================================================
    // Item (§3.3 / §4.2.3)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Item: bare item with parameters parses and round-trips")]
    public void Item_WithParameters_RoundTrip()
    {
        StructuredFieldItem.TryParse("5;foo=bar", out StructuredFieldItem item, out string? error).ShouldBeTrue(error);

        item.Value.AsInteger().ShouldBe(5);
        item.Parameters.TryGetValue("foo", out StructuredFieldBareItem foo).ShouldBeTrue();
        foo.AsToken().ShouldBe("bar");
        item.Serialize().ShouldBe("5;foo=bar");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Parameters: boolean-true parameter uses the bare-key short form")]
    public void Parameters_BooleanTrue_ShortForm()
    {
        StructuredFieldItem.TryParse("foo;bar;baz=1", out StructuredFieldItem item, out string? error).ShouldBeTrue(error);

        item.Parameters.Count.ShouldBe(2);
        item.Parameters.TryGetValue("bar", out StructuredFieldBareItem bar).ShouldBeTrue();
        bar.AsBoolean().ShouldBeTrue();
        item.Serialize().ShouldBe("foo;bar;baz=1");
    }

    // ============================================================================
    // Multi-line header combining (§4.2)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - SFV: multi-line list field is combined by comma")]
    public void MultiLine_List_CombinedByComma()
    {
        var value = new HttpHeaderValue(new[] { "1", "2", "3" });

        StructuredFieldList.TryParse(value, out StructuredFieldList list).ShouldBeTrue();

        list.Count.ShouldBe(3);
        list[0].Item.Value.AsInteger().ShouldBe(1);
        list[2].Item.Value.AsInteger().ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV: multi-line dictionary field is combined by comma")]
    public void MultiLine_Dictionary_CombinedByComma()
    {
        var value = new HttpHeaderValue(new[] { "a=1", "b=2" });

        StructuredFieldDictionary.TryParse(value, out StructuredFieldDictionary dict).ShouldBeTrue();

        dict.Count.ShouldBe(2);
        dict.ContainsKey("a").ShouldBeTrue();
        dict.ContainsKey("b").ShouldBeTrue();
    }

    // ============================================================================
    // Strict fail-parsing (§4.2)
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - SFV: malformed fields fail the whole parse")]
    [InlineData("a, b,")]           // trailing comma in list
    [InlineData("a b")]             // list members without a comma
    [InlineData("(a b")]            // unterminated inner list
    [InlineData("a=1,")]           // trailing comma in dictionary
    public void StrictFailParsing_ListAndDictionary_Fail(string input)
    {
        StructuredFieldList.TryParse(input, out _, out _).ShouldBeFalse();
        StructuredFieldDictionary.TryParse(input, out _, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Item: trailing content fails the parse")]
    public void Item_TrailingContent_Fails()
    {
        StructuredFieldItem.TryParse("5 6", out _, out string? error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Parse: throws HttpException on malformed input")]
    public void Parse_Malformed_ThrowsHttpException()
    {
        HttpException ex = Should.Throw<HttpException>(() => StructuredFieldItem.Parse("5 6"));
        ex.Code.ShouldBe(HttpErrorCode.InvalidStructuredField);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV Parse: string overload resolves to the span parser")]
    public void Parse_StringLiteral_ResolvesToSpan()
    {
        // Verifies the ReadOnlySpan<char> and HttpHeaderValue overloads are not ambiguous
        // for a string argument.
        StructuredFieldList list = StructuredFieldList.Parse("a, b");
        list.Count.ShouldBe(2);
    }

    // ============================================================================
    // Programmatic construction + serialization (§4.1)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - SFV: programmatic dictionary serializes canonically")]
    public void Programmatic_Dictionary_Serializes()
    {
        var members = new List<KeyValuePair<string, StructuredFieldMember>>
        {
            new("u", StructuredFieldMember.FromItem(new StructuredFieldItem(StructuredFieldBareItem.FromInteger(3)))),
            new("i", StructuredFieldMember.FromItem(new StructuredFieldItem(StructuredFieldBareItem.FromBoolean(true)))),
        };
        var dict = new StructuredFieldDictionary(members);

        dict.Serialize().ShouldBe("u=3, i");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV: programmatic item with parameters serializes canonically")]
    public void Programmatic_ItemWithParameters_Serializes()
    {
        var parameters = new StructuredFieldParameters(new[]
        {
            new KeyValuePair<string, StructuredFieldBareItem>("a", StructuredFieldBareItem.FromToken("b")),
        });
        var item = new StructuredFieldItem(StructuredFieldBareItem.FromString("hi"), parameters);

        item.Serialize().ShouldBe("\"hi\";a=b");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SFV: parse-serialize-parse is stable")]
    public void RoundTrip_ParseSerializeParse_IsStable()
    {
        const string input = "a=1, b=(x y);q=?0, c=@1659578233, d=:aGk=:, e=1.25";

        StructuredFieldDictionary.TryParse(input, out StructuredFieldDictionary first, out string? error).ShouldBeTrue(error);
        string serialized = first.Serialize();
        StructuredFieldDictionary.TryParse(serialized, out StructuredFieldDictionary second, out _).ShouldBeTrue();

        second.ShouldBe(first);
        second.Serialize().ShouldBe(serialized);
    }
}
