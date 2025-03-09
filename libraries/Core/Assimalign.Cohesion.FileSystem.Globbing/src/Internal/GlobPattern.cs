using System;
using System.IO;
using System.Collections.Generic;
using System.Data;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

using static System.IO.Glob;

internal abstract class GlobPattern : IGlobPattern
{
    public abstract Glob Glob { get; }
    public virtual void Declare(Action<Segment, bool> declare) { }
    public virtual bool Test(FileSystemPath path) { return false; }
    public abstract bool Test(IFileSystemFile file);
    public abstract bool Test(IFileSystemDirectory directory);
    public abstract void PushDirectory(IFileSystemDirectory directory);
    public virtual void PopDirectory()
    {
    }
}

internal abstract class GlobPattern<TFrame> : GlobPattern
{
    private Stack<TFrame> _stack = new Stack<TFrame>();
    protected TFrame? Frame;

    public override void PopDirectory()
    {
        Frame = _stack.Pop();
    }

    protected void PushDataFrame(TFrame frame)
    {
        _stack.Push(Frame!);
        Frame = frame;
    }
    protected bool IsStackEmpty()
    {
        return _stack.Count == 0;
    }
}
