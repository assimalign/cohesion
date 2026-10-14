using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Checks the default HPACK field's size and value semantics.</summary>
public class HPackHeaderFieldTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HPackHeaderField: Default field should preserve size overhead and value semantics")]
    public void Length_DefaultField_ShouldPreserveSizeOverheadAndValueSemantics()
    {
        // Arrange
        HPackHeaderField field = default;
        HPackHeaderField other = default;
        HPackHeaderField populated = new(null, "x"u8, "y"u8);

        // Act
        int length = field.Length;
        int hashCode = field.GetHashCode();
        string text = field.ToString();
        Dictionary<HPackHeaderField, int> dictionary = new() { [field] = 1 };
        HashSet<HPackHeaderField> set = new() { field };

        // Assert
        length.ShouldBe(HPackHeaderField.RfcOverhead);
        populated.Length.ShouldBe(HPackHeaderField.RfcOverhead + 2);
        hashCode.ShouldBe(other.GetHashCode());
        field.Equals(other).ShouldBeTrue();
        field.Equals((object)other).ShouldBeTrue();
        field.Equals(populated).ShouldBeFalse();
        text.ShouldBe("<empty>");
        dictionary[other].ShouldBe(1);
        dictionary.Remove(other).ShouldBeTrue();
        set.Contains(other).ShouldBeTrue();
        set.Add(other).ShouldBeFalse();
        set.Remove(other).ShouldBeTrue();
    }
}
