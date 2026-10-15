using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectPool.Tests;

public sealed class ObjectPoolRetentionTests
{
    [Theory(DisplayName = "Cohesion Test [ObjectPool] - Create: Rejects negative retention through every creation path")]
    [InlineData(0, -1)]
    [InlineData(1, -1)]
    [InlineData(2, -1)]
    [InlineData(3, -1)]
    [InlineData(4, -1)]
    [InlineData(0, int.MinValue)]
    [InlineData(1, int.MinValue)]
    [InlineData(2, int.MinValue)]
    [InlineData(3, int.MinValue)]
    [InlineData(4, int.MinValue)]
    public void Create_NegativeMaximumRetained_ThrowsArgumentOutOfRangeException(int creationPath, int maximumRetained)
    {
        // Arrange
        Action create = () => CreatePool(creationPath, maximumRetained);

        // Act
        var exception = Should.Throw<ArgumentOutOfRangeException>(create);

        // Assert
        exception.ParamName.ShouldBe("maximumRetained");
    }

    [Theory(DisplayName = "Cohesion Test [ObjectPool] - Create with arguments: Rejects negative retention through every creation path")]
    [InlineData(0, -1)]
    [InlineData(1, -1)]
    [InlineData(2, -1)]
    [InlineData(3, -1)]
    [InlineData(0, int.MinValue)]
    [InlineData(1, int.MinValue)]
    [InlineData(2, int.MinValue)]
    [InlineData(3, int.MinValue)]
    public void CreateWithArguments_NegativeMaximumRetained_ThrowsArgumentOutOfRangeException(int creationPath, int maximumRetained)
    {
        // Arrange
        Action create = () => CreatePoolWithArguments(creationPath, maximumRetained);

        // Act
        var exception = Should.Throw<ArgumentOutOfRangeException>(create);

        // Assert
        exception.ParamName.ShouldBe("maximumRetained");
    }

    [Theory(DisplayName = "Cohesion Test [ObjectPool] - Return: Zero retention never reuses an instance")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Return_ZeroMaximumRetained_DoesNotRetainObjects(int creationPath)
    {
        // Arrange
        var pool = CreatePool(creationPath, 0);
        var first = pool.Rent();
        var second = pool.Rent();

        // Act
        pool.Return(first);
        pool.Return(second);
        var next = pool.Rent();

        // Assert
        next.ShouldNotBeSameAs(first);
        next.ShouldNotBeSameAs(second);
    }

    [Theory(DisplayName = "Cohesion Test [ObjectPool] - Return with arguments: Zero retention never reuses an instance")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReturnWithArguments_ZeroMaximumRetained_DoesNotRetainObjects(int creationPath)
    {
        // Arrange
        var pool = CreatePoolWithArguments(creationPath, 0);
        var first = pool.Rent(1);
        var second = pool.Rent(2);

        // Act
        pool.Return(first);
        pool.Return(second);
        var next = pool.Rent(3);

        // Assert
        next.ShouldNotBeSameAs(first);
        next.ShouldNotBeSameAs(second);
    }

    [Theory(DisplayName = "Cohesion Test [ObjectPool] - Return: One retained object uses only the first slot")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Return_OneMaximumRetained_RetainsOnlyOneObject(int creationPath)
    {
        // Arrange
        var pool = CreatePool(creationPath, 1);
        var first = pool.Rent();
        var second = pool.Rent();

        // Act
        pool.Return(first);
        pool.Return(second);
        var retained = pool.Rent();
        var next = pool.Rent();

        // Assert
        retained.ShouldBeSameAs(first);
        next.ShouldNotBeSameAs(first);
        next.ShouldNotBeSameAs(second);
    }

    [Theory(DisplayName = "Cohesion Test [ObjectPool] - Return with arguments: One retained object uses only the first slot")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReturnWithArguments_OneMaximumRetained_RetainsOnlyOneObject(int creationPath)
    {
        // Arrange
        var pool = CreatePoolWithArguments(creationPath, 1);
        var first = pool.Rent(1);
        var second = pool.Rent(2);

        // Act
        pool.Return(first);
        pool.Return(second);
        var retained = pool.Rent(3);
        var next = pool.Rent(4);

        // Assert
        retained.ShouldBeSameAs(first);
        next.ShouldNotBeSameAs(first);
        next.ShouldNotBeSameAs(second);
    }

    private static ObjectPool<object> CreatePool(int creationPath, int maximumRetained) => creationPath switch
    {
        0 => ObjectPool<object>.Create(maximumRetained),
        1 => ObjectPool<object>.Create(new TestObjectFactory(), maximumRetained),
        2 => ObjectPool<object>.Create(new TestObjectFactory(), ObjectPoolPolicy<object>.Default, maximumRetained),
        3 => new DefaultObjectPool<object>(new TestObjectFactory(), maximumRetained),
        4 => new DefaultObjectPool<object>(new TestObjectFactory(), ObjectPoolPolicy<object>.Default, maximumRetained),
        _ => throw new ArgumentOutOfRangeException(nameof(creationPath))
    };

    private static ObjectPool<object, int> CreatePoolWithArguments(int creationPath, int maximumRetained) => creationPath switch
    {
        0 => ObjectPool<object, int>.Create(new TestObjectFactoryWithArguments(), maximumRetained),
        1 => ObjectPool<object, int>.Create(new TestObjectFactoryWithArguments(), ObjectPoolPolicy<object>.Default, maximumRetained),
        2 => new DefaultObjectPool<object, int>(new TestObjectFactoryWithArguments(), maximumRetained),
        3 => new DefaultObjectPool<object, int>(new TestObjectFactoryWithArguments(), ObjectPoolPolicy<object>.Default, maximumRetained),
        _ => throw new ArgumentOutOfRangeException(nameof(creationPath))
    };

    private sealed class TestObjectFactory : ObjectPoolFactory<object>
    {
        public override object Create() => new object();
    }

    private sealed class TestObjectFactoryWithArguments : ObjectPoolFactory<object, int>
    {
        public override object Create(int args) => new object();
    }
}
