using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Execution.Tests;

public sealed class DatabaseRowTests
{
    [Fact(DisplayName = "Cohesion Test [Execution] - Rows opt into MISSING and unsupported rows refuse the accessor")]
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

    [Theory(DisplayName = "Cohesion Test [Execution] - The MISSING accessor validates bounds before calling the leaf")]
    [InlineData(-1)]
    [InlineData(1)]
    public void IsMissing_InvalidOrdinal_ShouldRejectBeforeCore(int ordinal)
    {
        // Arrange
        var row = new TestRow(supportsMissing: true);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => row.IsMissing(ordinal));
        row.CoreCalls.ShouldBe(0);
    }

    private sealed class TestRow : DatabaseRow
    {
        internal TestRow(bool supportsMissing = false) : base(supportsMissing) { }
        internal int CoreCalls { get; private set; }
        protected override bool IsMissingCore(int ordinal)
        {
            CoreCalls++;
            return base.IsMissingCore(ordinal);
        }
        public override int FieldCount => 1;
        public override bool IsNull(int ordinal) => true;
        public override ReadOnlyMemory<byte> GetBytes(int ordinal) => default;
        public override string? GetString(int ordinal) => null;
        public override int GetInt32(int ordinal) => 0;
        public override long GetInt64(int ordinal) => 0;
        public override bool GetBoolean(int ordinal) => false;
        public override double GetDouble(int ordinal) => 0;
        public override object? GetValue(int ordinal) => null;
    }
}
