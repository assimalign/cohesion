using System;
using System.IO;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.Tests;

public class FileSystemExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [FileSystem] - Exception: ctor stores error code and message")]
    public void Ctor_StoresCodeAndMessage()
    {
        var exception = new FileSystemException(FileSystemErrorCode.NotFound, "missing");

        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
        Assert.Equal("missing", exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Exception: defaults to Other when only message supplied")]
    public void Ctor_OtherCodeWhenOnlyMessage()
    {
        var exception = new FileSystemException("oops");

        Assert.Equal(FileSystemErrorCode.Other, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - Exception: preserves inner exception")]
    public void Ctor_PreservesInner()
    {
        var inner = new IOException("inner");
        var exception = new FileSystemException(FileSystemErrorCode.AccessDenied, "denied", inner);

        Assert.Same(inner, exception.InnerException);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowDirectoryNotFound: produces NotFound + wraps DirectoryNotFoundException")]
    public void ThrowDirectoryNotFound_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowDirectoryNotFound("/missing"));

        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
        Assert.IsType<DirectoryNotFoundException>(exception.InnerException);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowFileNotFound: produces NotFound + wraps FileNotFoundException")]
    public void ThrowFileNotFound_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowFileNotFound("/missing.txt"));

        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
        Assert.IsType<FileNotFoundException>(exception.InnerException);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowPathNotFound: produces NotFound (generic)")]
    public void ThrowPathNotFound_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowPathNotFound("/missing"));

        Assert.Equal(FileSystemErrorCode.NotFound, exception.Code);
        Assert.Contains("/missing", exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowPathTooLong: produces PathTooLong + wraps PathTooLongException")]
    public void ThrowPathTooLong_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowPathTooLong("/very/long/path"));

        Assert.Equal(FileSystemErrorCode.PathTooLong, exception.Code);
        Assert.IsType<PathTooLongException>(exception.InnerException);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowAccessDenied: produces AccessDenied + wraps UnauthorizedAccessException")]
    public void ThrowAccessDenied_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowAccessDenied("/private"));

        Assert.Equal(FileSystemErrorCode.AccessDenied, exception.Code);
        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowPathInUse: produces PathInUse")]
    public void ThrowPathInUse_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowPathInUse("/locked"));

        Assert.Equal(FileSystemErrorCode.PathInUse, exception.Code);
        Assert.Contains("/locked", exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowPathAlreadyExist: produces Conflict")]
    public void ThrowPathAlreadyExist_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowPathAlreadyExist("/already.txt"));

        Assert.Equal(FileSystemErrorCode.Conflict, exception.Code);
        Assert.Contains("/already.txt", exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowNotEnoughSpace: produces NotEnoughSpace")]
    public void ThrowNotEnoughSpace_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowNotEnoughSpace());

        Assert.Equal(FileSystemErrorCode.NotEnoughSpace, exception.Code);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowReadOnly: produces ReadOnly with operation hint")]
    public void ThrowReadOnly_Maps()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowReadOnly("CreateFile"));

        Assert.Equal(FileSystemErrorCode.ReadOnly, exception.Code);
        Assert.Contains("CreateFile", exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [FileSystem] - ThrowReadOnly: empty operation falls back to generic message")]
    public void ThrowReadOnly_EmptyOperation_GenericMessage()
    {
        var exception = Assert.Throws<FileSystemException>(() =>
            FileSystemException.ThrowReadOnly(string.Empty));

        Assert.Equal(FileSystemErrorCode.ReadOnly, exception.Code);
        Assert.Contains("read-only", exception.Message);
    }

    [Theory(DisplayName = "Cohesion Test [FileSystem] - FileSystemErrorCode: ordinal values are stable")]
    [InlineData(FileSystemErrorCode.Other, 0)]
    [InlineData(FileSystemErrorCode.NotFound, 1)]
    [InlineData(FileSystemErrorCode.Conflict, 2)]
    [InlineData(FileSystemErrorCode.PathTooLong, 3)]
    [InlineData(FileSystemErrorCode.NotEnoughSpace, 4)]
    [InlineData(FileSystemErrorCode.AccessDenied, 5)]
    [InlineData(FileSystemErrorCode.PathInUse, 6)]
    [InlineData(FileSystemErrorCode.ReadOnly, 7)]
    public void ErrorCode_OrdinalStable(FileSystemErrorCode code, int ordinal)
    {
        Assert.Equal(ordinal, (int)code);
    }
}
