using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing.PatternContexts;

public abstract class FilePatternContext<TFrame> : IFilePatternContext
{
    private Stack<TFrame> _stack = new Stack<TFrame>();
    protected TFrame Frame;

    public virtual void Declare(Action<IFilePathSegment, bool> declare) { }

    public abstract FilePatternTestResult Test(IFileSystemFile file);

    public abstract bool Test(IFileSystemDirectory directory);

    public abstract void PushDirectory(IFileSystemDirectory directory);

    public virtual void PopDirectory()
    {
        Frame = _stack.Pop();
    }

    protected void PushDataFrame(TFrame frame)
    {
        _stack.Push(Frame);
        Frame = frame;
    }

    protected bool IsStackEmpty()
    {
        return _stack.Count == 0;
    }
}
