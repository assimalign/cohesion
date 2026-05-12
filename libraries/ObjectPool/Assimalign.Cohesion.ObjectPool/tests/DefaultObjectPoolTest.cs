using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectPool.Tests;

public class DefaultObjectPoolTest
{
    [Fact]
    public void DefaultObjectPoolWithDefaultPolicy_GetAnd_ReturnObject_SameInstance()
    {
        // Arrange
        var pool = ObjectPool<object>.Create();

        var obj1 = pool.Rent();
        pool.Return(obj1);

        // Act
        var obj2 = pool.Rent();

        // Assert
        Assert.Same(obj1, obj2);
    }

    [Fact]
    public void DefaultObjectPool_GetAndReturnObject_SameInstance()
    {
        // Arrange
        var pool = ObjectPool<List<int>>.Create(new ListFactory(), new ListPolicy());

        var list1 = pool.Rent();
        pool.Return(list1);

        // Act
        var list2 = pool.Rent();

        // Assert
        Assert.Same(list1, list2);
    }

    [Fact]
    public void DefaultObjectPool_CreatedByPolicy()
    {
        // Arrange
        var pool = ObjectPool<List<int>>.Create(new ListFactory());

        // Act
        var list = pool.Rent();

        // Assert
        Assert.Equal(17, list.Capacity);
    }

    [Fact]
    public void DefaultObjectPool_Return_RejectedByPolicy()
    {
        // Arrange
        var pool = ObjectPool<List<int>>.Create(new ListPolicy());
        var list1 = pool.Rent();
        list1.Capacity = 20;

        // Act
        pool.Return(list1);
        var list2 = pool.Rent();

        // Assert
        Assert.NotSame(list1, list2);
    }

    //[Fact]
    //public static void DefaultObjectPool_Honors_IResettable()
    //{
    //    var p = new DefaultObjectPool<Resettable>(new DefaultPooledObjectPolicy<Resettable>());
    //    var r = new Resettable();

    //    r.ResetReturnValue = false;
    //    p.Return(r);
    //    Assert.Equal(1, r.ResetCallCount);
    //    Assert.NotSame(r, p.Get());

    //    r.ResetReturnValue = true;
    //    p.Return(r);
    //    Assert.Equal(2, r.ResetCallCount);
    //    Assert.Same(r, p.Get());
    //}


    private class ListFactory : ObjectPoolFactory<List<int>>
    {
        public override List<int> Create()
        {
            return new List<int>(17);
        }
    }

    private class ListPolicy : ObjectPoolPolicy<List<int>>
    {
        public override bool CanReturn(List<int> obj)
        {
            return obj.Capacity == 17;
        }
    }

}
