using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal abstract class GlobPatternContext<TFrame> : IGlobPatternContext
{
    private Stack<TFrame> _stack = new Stack<TFrame>();
    protected TFrame? Frame;

    public virtual void Declare(Action<FileSystemPathSegment, bool> declare) { }

    public abstract GlobPatternTestResult Test(IFileSystemFile file);

    public abstract bool Test(IFileSystemDirectory directory);

    public abstract void PushDirectory(IFileSystemDirectory directory);

    public virtual void PopDirectory()
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
