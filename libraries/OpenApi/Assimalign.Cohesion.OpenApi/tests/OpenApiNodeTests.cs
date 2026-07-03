using Shouldly;
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

        node.Keys.ShouldBe(new[] { "zebra", "apple", "mango" });
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: value preserves scalar kind")]
    public void ValueNode_PreservesScalarKind()
    {
        OpenApiValueNode.Integer(7).Kind.ShouldBe(OpenApiValueKind.Integer);
        OpenApiValueNode.Double(1.5).Kind.ShouldBe(OpenApiValueKind.Double);
        OpenApiValueNode.Boolean(true).Kind.ShouldBe(OpenApiValueKind.Boolean);
        OpenApiValueNode.String("x").Kind.ShouldBe(OpenApiValueKind.String);
        OpenApiValueNode.Null.IsNull.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: integer is readable as double")]
    public void ValueNode_Integer_ReadableAsDouble()
    {
        OpenApiValueNode.Integer(42).GetDouble().ShouldBe(42d);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: implicit conversions produce expected kinds")]
    public void ImplicitConversions_ProduceExpectedKinds()
    {
        OpenApiNode fromString = "hello";
        OpenApiNode fromBool = true;
        OpenApiNode fromInt = 5;
        OpenApiNode fromDouble = 2.5;
        OpenApiNode fromNull = (string?)null;

        ((OpenApiValueNode)fromString).Kind.ShouldBe(OpenApiValueKind.String);
        ((OpenApiValueNode)fromBool).Kind.ShouldBe(OpenApiValueKind.Boolean);
        ((OpenApiValueNode)fromInt).Kind.ShouldBe(OpenApiValueKind.Integer);
        ((OpenApiValueNode)fromDouble).Kind.ShouldBe(OpenApiValueKind.Double);
        ((OpenApiValueNode)fromNull).IsNull.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Node: duplicate object key rejected by Add")]
    public void ObjectNode_DuplicateAdd_Throws()
    {
        var node = new OpenApiObjectNode();
        node.Add("k", "v");

        var act = () => node.Add("k", "other");

        Should.Throw<System.ArgumentException>(act);
    }
}
