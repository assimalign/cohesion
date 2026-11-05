using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Execution.Tests;

public sealed class DatabaseRowTests
{
    [Fact(DisplayName = "Cohesion Test [Execution] - Unsupported rows refuse MISSING for a valid ordinal")]
    public void IsMissing_UnsupportedRow_ShouldThrowBeforeCallingCore()
    {
        // Arrange
        var row = new TestRow();

        // Act / Assert
        row.SupportsMissing.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => row.IsMissing(0));
        row.CoreCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Execution] - A capability without its core refuses MISSING")]
    public void IsMissing_DefaultCore_ShouldThrow()
    {
        // Arrange
        var row = new TestRow(supportsMissing: true);

        // Act / Assert
        row.SupportsMissing.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => row.IsMissing(0));
        row.CoreCalls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Execution] - Every ordinal accessor rejects before any value core dispatch")]
    [InlineData(-1, 1, false)]
    [InlineData(1, 1, false)]
    [InlineData(-1, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(int.MinValue, 1, true)]
    [InlineData(int.MaxValue, 1, true)]
    [InlineData(0, 0, true)]
    [InlineData(0, 0, false)]
    public void Accessors_InvalidOrdinal_ShouldRejectBeforeCore(int ordinal, int fieldCount, bool supportsMissing)
    {
        // Arrange
        var row = new TestRow(supportsMissing, fieldCount);
        Action[] accessors = [() => row.IsNull(ordinal), () => row.IsMissing(ordinal),
            () => row.GetValue(ordinal), () => row.GetBytes(ordinal), () => row.GetString(ordinal),
            () => row.GetInt32(ordinal), () => row.GetInt64(ordinal),
            () => row.GetBoolean(ordinal), () => row.GetDouble(ordinal)];

        // Act / Assert
        foreach (var accessor in accessors)
        {
            Should.Throw<ArgumentOutOfRangeException>(accessor).ParamName.ShouldBe("ordinal");
        }
        row.CoreCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Execution] - Base-typed row access calls each value core once and retains its result")]
    public void Accessors_ValidOrdinal_ShouldReturnCoreValues()
    {
        // Arrange
        var leaf = new TestRow();
        DatabaseRow row = leaf;

        // Act / Assert
        row.IsNull(0).ShouldBeFalse();
        row.GetBytes(0).ToArray().ShouldBe(new byte[] { 42 });
        row.GetString(0).ShouldBe("42");
        row.GetInt32(0).ShouldBe(42);
        row.GetInt64(0).ShouldBe(42);
        row.GetBoolean(0).ShouldBeTrue();
        row.GetDouble(0).ShouldBe(42.5d);
        row.GetValue(0).ShouldBe("value");
        leaf.CoreCalls.ShouldBe(8);
    }

    private sealed class TestRow : DatabaseRow
    {
        private readonly int _fieldCount;
        private readonly byte[] _bytes = [42];

        internal TestRow(bool supportsMissing = false, int fieldCount = 1) : base(supportsMissing)
        {
            _fieldCount = fieldCount;
        }

        internal int CoreCalls { get; private set; }
        public override int FieldCount => _fieldCount;
        protected override bool IsMissingCore(int ordinal) { CoreCalls++; return base.IsMissingCore(ordinal); }
        protected override bool IsNullCore(int ordinal) { CoreCalls++; return false; }
        protected override ReadOnlyMemory<byte> GetBytesCore(int ordinal) { CoreCalls++; return _bytes; }
        protected override string? GetStringCore(int ordinal) { CoreCalls++; return "42"; }
        protected override int GetInt32Core(int ordinal) { CoreCalls++; return 42; }
        protected override long GetInt64Core(int ordinal) { CoreCalls++; return 42; }
        protected override bool GetBooleanCore(int ordinal) { CoreCalls++; return true; }
        protected override double GetDoubleCore(int ordinal) { CoreCalls++; return 42.5d; }
        protected override object? GetValueCore(int ordinal) { CoreCalls++; return "value"; }
    }
}
