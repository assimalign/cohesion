using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class DatabaseDebuggerViewTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Debugger: names project visible state without exposing private storage")]
    public void DebugView_DatabaseName_ShouldProjectVisibleState()
    {
        // Arrange
        var name = new DatabaseName("debugger-name");
        // Act
        object view = CreateView(name, out Type type);
        // Assert
        type.IsNestedPrivate.ShouldBeTrue();
        type.GetProperty(nameof(DatabaseName.Length))!.GetValue(view).ShouldBe(name.Length);
        type.GetProperty(nameof(DatabaseName.IsEmpty))!.GetValue(view).ShouldBe(false);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            property.SetMethod.ShouldBeNull();
        }
        type.GetFields(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
        name.ToString().ShouldBe("debugger-name");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Debugger: storage inspection performs no delegated stream reads or I/O")]
    public void DebugView_StorageStream_ShouldNotInspectUnderlyingStream()
    {
        // Arrange
        using var underlying = new PoisonStream();
        using var storage = new StorageStream(underlying);
        underlying.RejectInspection = true;
        // Act
        object view = CreateView(storage, out Type type);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            _ = property.GetValue(view);
        }
        // Assert
        underlying.InspectionCount.ShouldBe(0);
        type.GetFields(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Debugger: exception types retain their existing debugger behavior")]
    [InlineData(typeof(DatabaseException))]
    [InlineData(typeof(DatabaseNotFoundException))]
    public void DebuggerAttributes_ExceptionType_ShouldRemainUnmodified(Type type)
    {
        // Arrange / Act / Assert
        type.GetCustomAttribute<DebuggerDisplayAttribute>(inherit: false).ShouldBeNull();
        type.GetCustomAttribute<DebuggerTypeProxyAttribute>(inherit: false).ShouldBeNull();
    }

    private static object CreateView(object target,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] out Type proxyType)
    {
        Type type = target.GetType();
        var attribute = type.GetCustomAttribute<DebuggerTypeProxyAttribute>(inherit: true);
        attribute.ShouldNotBeNull();
        proxyType = Type.GetType(attribute.ProxyTypeName, throwOnError: true)!;
        return Activator.CreateInstance(proxyType, target)!;
    }

    private sealed class PoisonStream : Stream
    {
        public bool RejectInspection { get; set; }
        public int InspectionCount { get; private set; }
        public override bool CanRead => Inspect(true);
        public override bool CanSeek => Inspect(true);
        public override bool CanWrite => Inspect(true);
        public override long Length => Inspect(0L);
        public override long Position { get => Inspect(0L); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private T Inspect<T>(T value)
        {
            if (RejectInspection)
            {
                InspectionCount++;
                throw new InvalidOperationException();
            }
            return value;
        }
    }
}
