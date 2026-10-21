using System;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCastLookupTests
{
    private readonly ITestOutputHelper _output;

    public SqlCastLookupTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST lookup allocates no keys for built-ins or domains (#1422)")]
    public void ResolveCast_RepeatedLookup_ShouldAllocateNothing()
    {
        // Arrange
        var source = SqlType.CreateDomain("source_domain", SqlType.Integer);
        var target = SqlType.CreateDomain("target_domain", SqlType.Text);
        var catalog = new SqlTypeCollection(null).Add(source).Add(target).Freeze();
        const int iterations = 1000;
        Action lookup = () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                _ = catalog.FindCast(SqlType.Integer, SqlType.Text);
                _ = catalog.ResolveCast(null, DatabaseType.Int32, SqlType.Text);
                _ = catalog.ResolveCast(source, DatabaseType.Int32, target);
            }
        };

        // Act
        long allocated = MinimumAllocation(lookup);
        _output.WriteLine($"FindCast + built-in ResolveCast + domain fallback: {allocated / (double)iterations} bytes/iteration");

        // Assert
        allocated.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST binding allocates only its result vector (#1422)")]
    public void ResolveCasts_RepeatedBinding_ShouldAllocateOnlyResultArray()
    {
        // Arrange
        var source = SqlType.CreateDomain("source_domain", SqlType.Integer);
        var target = SqlType.CreateDomain("target_domain", SqlType.Text);
        var catalog = new SqlTypeCollection(null).Add(source).Add(target).Freeze();
        const int iterations = 1000;
        int count = catalog.ResolveCasts(source, target).Length;
        Action bind = () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                GC.KeepAlive(catalog.ResolveCasts(null, SqlType.Text));
                GC.KeepAlive(catalog.ResolveCasts(source, target));
            }
        };
        Action arrays = () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                GC.KeepAlive(new SqlCast?[count]);
                GC.KeepAlive(new SqlCast?[count]);
            }
        };

        // Act
        long allocated = MinimumAllocation(bind);
        long arrayAllocation = MinimumAllocation(arrays);
        _output.WriteLine($"Built-in + domain ResolveCasts: {allocated / (double)iterations} bytes/pair; result vectors: {arrayAllocation / (double)iterations} bytes/pair");

        // Assert
        allocated.ShouldBe(arrayAllocation);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST keys preserve case-insensitive domain identity and modifiers (#1422)")]
    public void FindCast_DomainNamesAndStorageModifiers_ShouldPreserveIdentity()
    {
        // Arrange
        var source = SqlType.CreateSqlDomain("a|B:9", SqlType.Integer, null, null, false);
        var target = SqlType.CreateSqlDomain("b:9|a", SqlType.Text, null, null, false);
        var fromDomain = SqlCast.Create<int, string>(source, target, static value => value.ToString(), SqlCastContext.Explicit);
        var fromBase = SqlCast.Create<int, string>(SqlType.Integer, target, static value => value.ToString(), SqlCastContext.Explicit);
        var catalog = new SqlTypeCollection(null).Add(source).Add(target).AddCast(fromDomain).AddCast(fromBase).Freeze();
        var sameName = SqlType.CreateSqlDomain("A|b:9", SqlType.Integer, null, null, false);

        // Act / Assert
        catalog.FindCast(sameName, target).ShouldBeSameAs(fromDomain);
        catalog.ResolveCast(sameName, DatabaseType.Int32, target).ShouldBeSameAs(fromDomain);
        catalog.ResolveCast(null, DatabaseType.Int32, target).ShouldBeSameAs(fromBase);
        catalog.ResolveCast(source, DatabaseType.Int32, sameName).ShouldBeNull();
        catalog.FindCast(SqlType.Integer, SqlType.VarChar(10)).ShouldBeSameAs(catalog.FindCast(SqlType.Integer, SqlType.Text));
        catalog.FindCast(target, source).ShouldBeNull();
    }

    private static long MinimumAllocation(Action action)
    {
        action();
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return minimum;
    }
}
