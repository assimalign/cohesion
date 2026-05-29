using FluentAssertions;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Tests;

public class OpenApiNodeTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: object preserves insertion order")]
    public void ObjectNode_PreservesInsertionOrder()
    {
        var node = new OpenApiObjectNode
        {
            ["zebra"] = "z",
            ["apple"] = "a",
            ["mango"] = "m"
        };

        node.Keys.Should().Equal("zebra", "apple", "mango");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: value preserves scalar kind")]
    public void ValueNode_PreservesScalarKind()
    {
        OpenApiValueNode.Integer(7).Kind.Should().Be(OpenApiValueKind.Integer);
        OpenApiValueNode.Double(1.5).Kind.Should().Be(OpenApiValueKind.Double);
        OpenApiValueNode.Boolean(true).Kind.Should().Be(OpenApiValueKind.Boolean);
        OpenApiValueNode.String("x").Kind.Should().Be(OpenApiValueKind.String);
        OpenApiValueNode.Null.IsNull.Should().BeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: integer is readable as double")]
    public void ValueNode_Integer_ReadableAsDouble()
    {
        OpenApiValueNode.Integer(42).GetDouble().Should().Be(42d);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: implicit conversions produce expected kinds")]
    public void ImplicitConversions_ProduceExpectedKinds()
    {
        OpenApiNode fromString = "hello";
        OpenApiNode fromBool = true;
        OpenApiNode fromInt = 5;
        OpenApiNode fromDouble = 2.5;
        OpenApiNode fromNull = (string?)null;

        ((OpenApiValueNode)fromString).Kind.Should().Be(OpenApiValueKind.String);
        ((OpenApiValueNode)fromBool).Kind.Should().Be(OpenApiValueKind.Boolean);
        ((OpenApiValueNode)fromInt).Kind.Should().Be(OpenApiValueKind.Integer);
        ((OpenApiValueNode)fromDouble).Kind.Should().Be(OpenApiValueKind.Double);
        ((OpenApiValueNode)fromNull).IsNull.Should().BeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: duplicate object key rejected by Add")]
    public void ObjectNode_DuplicateAdd_Throws()
    {
        var node = new OpenApiObjectNode();
        node.Add("k", "v");

        var act = () => node.Add("k", "other");

        act.Should().Throw<System.ArgumentException>();
    }
}
