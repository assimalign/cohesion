using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentRegisteredFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: argument masks reject None and unknown bits")]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(255)]
    public void Create_InvalidArgumentMask_ShouldRefuse(int value)
        => Should.Throw<ArgumentException>(() => new DocumentArgumentMaskFunction((DocumentFunctionArgumentKind)value));

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: a valued-kind union admits only its declared kinds")]
    public void Invoke_ArgumentKindUnion_ShouldKeepDeclaredAdmission()
    {
        var mask = DocumentFunctionArgumentKind.Boolean | DocumentFunctionArgumentKind.String;
        var function = new DocumentArgumentMaskFunction(mask);
        function.ArgumentKinds.ShouldHaveSingleItem().ShouldBe(mask);
        function.Invoke(new DocumentArguments(new[] { DocumentValue.FromBoolean(true) })).AsString().ShouldBe("accepted");
        function.Invoke(new DocumentArguments(new[] { DocumentValue.FromString("text") })).AsString().ShouldBe("accepted");
        function.Invoke(new DocumentArguments(new[] { DocumentValue.FromDecimal(1) })).IsNull.ShouldBeTrue();
        function.Invoke(new DocumentArguments(new[] { DocumentValue.Missing })).IsMissing.ShouldBeTrue();
    }
}
