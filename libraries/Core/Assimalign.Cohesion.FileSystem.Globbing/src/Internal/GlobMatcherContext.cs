using System;
using System.Linq;
using System.Collections.Generic;

using static System.IO.Glob;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

using Utilities;

internal class GlobMatcherContext
{
    private readonly IEnumerable<GlobContext> _includes;
    private readonly IEnumerable<GlobContext> _excludes;
    private readonly List<IFileSystemInfo> _results { get; }

    private readonly HashSet<string> _declaredLiteralFolderSegmentInString;
    private readonly HashSet<Segment> _declaredLiteralFolderSegments = new HashSet<Segment>();
    private readonly HashSet<Segment> _declaredLiteralFileSegments = new HashSet<Segment>();

    private bool _declaredParentPathSegment;
    private bool _declaredWildcardPathSegment;

    public GlobMatcherContext(
        IEnumerable<GlobContext> includes,
        IEnumerable<GlobContext> excludes,
        StringComparison comparison)
    {
        _results = new List<IFileSystemInfo>();
        _includes = includes;
        _excludes = excludes;
        _declaredLiteralFolderSegmentInString = new HashSet<string>(
            StringComparisonHelper.GetStringComparer(comparison));
    }

    public GlobMatchResults Match(IFileSystemDirectory directory)
    {
        _results.Clear();

        Execute(directory);

        return new GlobMatchResults(_results);
    }

    // tries to match any pattern to the given directory
    public GlobMatchResults MatchExact(IFileSystemDirectory directory)
    {;
        PushDirectory(directory);
        Declare();

        if (MatchPatternContexts(directory, (pattern, dir) => pattern.Test((dir as IFileSystemDirectory)!)))
        {
            return new GlobMatchResults([directory]);
        }

        PopDirectory();

        return GlobMatchResults.Empty;
    }

    // tries to match any pattern to the given file
    public GlobMatchResults MatchExact(IFileSystemFile file)
    {
        PushDirectory(file.Directory);
        Declare();

        if (MatchPatternContexts(file, (pattern, file) => pattern.Test((file as IFileSystemFile)!)))
        {
            return new GlobMatchResults([file]);
        }

        PopDirectory();

        return GlobMatchResults.Empty;
    }

    private void Execute(IFileSystemDirectory directory)
    {
        // Request all the including and excluding patterns to push current directory onto their status stack.
        PushDirectory(directory);
        Declare();

        var entities = new List<IFileSystemInfo>();

        if (_declaredWildcardPathSegment || _declaredLiteralFileSegments.Any())
        {
            entities.AddRange(directory);
        }
        else
        {
            foreach (var candidate in directory)
            {
                if (_declaredLiteralFolderSegmentInString.Contains(candidate.Path))
                {
                    entities.Add(candidate);
                }
            }
        }

        if (_declaredParentPathSegment)
        {
            entities.Add(directory.GetDirectory(".."));
        }

        // collect files and sub directories
        var children = new List<IFileSystemDirectory>();

        foreach (var info in entities)
        {
            if (info is IFileSystemDirectory directoryInfo)
            {
                if (MatchPatternContexts(directoryInfo, (pattern, dir) => pattern.Test((dir as IFileSystemDirectory)!)))
                {
                    children.Add(directoryInfo);
                }

                continue;
            }
            if (info is IFileSystemFile fileInfo)
            {
                if (MatchPatternContexts(fileInfo, (pattern, file) => pattern.Test((file as IFileSystemFile)!)))
                {
                    _results.Add(fileInfo);
                }

                continue;
            }
        }

        // Matches the sub directories recursively
        foreach (var child in children)
        {
            Execute(child);
        }

        // Request all the including and excluding patterns to pop their status stack.
        PopDirectory();
    }
    private void Declare()
    {
        _declaredLiteralFileSegments.Clear();
        _declaredLiteralFolderSegments.Clear();
        _declaredParentPathSegment = false;
        _declaredWildcardPathSegment = false;

        foreach (GlobContext include in _includes)
        {
            include.Declare(DeclareInclude);
        }
    }
    private void DeclareInclude(Segment segment, bool isLastSegment)
    {
        if (segment.Kind.Equals(SegmentKind.Literal))
        {
            if (isLastSegment)
            {
                _declaredLiteralFileSegments.Add(segment);
            }
            else
            {
                _declaredLiteralFolderSegments.Add(segment);
                _declaredLiteralFolderSegmentInString.Add(segment.Value);
            }
        }
        else if (segment.Kind.Equals(SegmentKind.ParentDirectory))
        {
            _declaredParentPathSegment = true;
        }
        else if (segment.Kind.Equals(SegmentKind.Wildcard))
        {
            _declaredWildcardPathSegment = true;
        }
    }
    
    internal static string CombinePath(string left, string right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return right;
        }
        else
        {
            return $"{left}/{right}";
        }
    }

    private bool MatchPatternContexts(IFileSystemInfo fileinfo, Func<GlobContext, IFileSystemInfo, bool> test)
    {
        var result = false;

        // If the given file/directory matches any including pattern, continues to next step.
        foreach (GlobContext pattern in _includes)
        {
            var localResult = test(pattern, fileinfo);
            if (localResult)
            {
                result = localResult;
                break;
            }
        }

        // If the given file/directory doesn't match any of the including pattern, returns false.
        if (!result)
        {
            return false;
        }

        // If the given file/directory matches any excluding pattern, returns false.
        foreach (GlobContext pattern in _excludes)
        {
            if (test(pattern, fileinfo))
            {
                return false;
            }
        }

        return result;
    }
    private void PopDirectory()
    {
        foreach (IGlobPatternContext context in _excludes)
        {
            context.PopDirectory();
        }

        foreach (IGlobPatternContext context in _includes)
        {
            context.PopDirectory();
        }
    }
    private void PushDirectory(IFileSystemDirectory directory)
    {
        foreach (GlobContext context in _includes)
        {
            context.PushDirectory(directory);
        }

        foreach (GlobContext context in _excludes)
        {
            context.PushDirectory(directory);
        }
    }
}
