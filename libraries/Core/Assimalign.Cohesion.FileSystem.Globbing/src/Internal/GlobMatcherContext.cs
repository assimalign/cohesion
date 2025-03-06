using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Assimalign.Cohesion.FileSystem.FileSystemPathSegment;
using Assimalign.Cohesion.FileSystem.Globbing.Internal.Utilities;
using System.IO;
using System.Reflection;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

internal class GlobMatcherContext
{
    private readonly List<IGlobPatternContext> _includes;
    private readonly List<IGlobPatternContext> _excludes;
    private readonly List<GlobPatternMatch> _files;

    private readonly List<IFileSystemInfo> _results = new List<IFileSystemInfo>();

    private readonly HashSet<string> _declaredLiteralFolderSegmentInString;
    private readonly HashSet<LiteralSegment> _declaredLiteralFolderSegments = new HashSet<LiteralSegment>();
    private readonly HashSet<LiteralSegment> _declaredLiteralFileSegments = new HashSet<LiteralSegment>();

    private bool _declaredParentPathSegment;
    private bool _declaredWildcardPathSegment;

    public GlobMatcherContext(
        IEnumerable<IGlobPattern> includes,
        IEnumerable<IGlobPattern> excludes,
        StringComparison comparison)
    {
        _files = new List<GlobPatternMatch>();
        _includes = includes.Select<IGlobPattern, IGlobPatternContext>(pattern => pattern switch
        {
            ILinearGlobPattern linear => new GlobPatternContextLinearInclude(linear, comparison),
            IRaggedGlobPattern ragged => new GlobPatternContextRaggedInclude(ragged, comparison),
        }).ToList();
        _excludes = excludes.Select<IGlobPattern, IGlobPatternContext>(pattern => pattern switch
        {
            ILinearGlobPattern linear => new GlobPatternContextLinearExclude(linear, comparison),
            IRaggedGlobPattern ragged => new GlobPatternContextRaggedExclude(ragged, comparison),
        }).ToList();
        _declaredLiteralFolderSegmentInString = new HashSet<string>(StringComparisonHelper.GetStringComparer(comparison));
    }

    public GlobPatternMatchingResult Match(IFileSystemDirectory directory)
    {
        _results.Clear();

        Execute(directory);

        return new GlobPatternMatchingResult(_results);
    }

    // tries to match any pattern to the given directory
    public GlobPatternMatchingResult MatchExact(IFileSystemDirectory directory)
    {;
        PushDirectory(directory);
        Declare();

        if (MatchPatternContexts(directory, (pattern, dir) => pattern.Test((dir as IFileSystemDirectory)!)))
        {
            return new GlobPatternMatchingResult([directory]);
        }

        PopDirectory();

        return GlobPatternMatchingResult.Empty;
    }

    // tries to match any pattern to the given file
    public GlobPatternMatchingResult MatchExact(IFileSystemFile file)
    {
        PushDirectory(file.Directory);
        Declare();

        GlobPatternTestResult result = MatchPatternContexts(file, (pattern, file) => pattern.Test((file as IFileSystemFile)!));

        if (result.IsSuccessful)
        {
            return new GlobPatternMatchingResult([file]);
        }

        PopDirectory();

        return GlobPatternMatchingResult.Empty;
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
                GlobPatternTestResult result = MatchPatternContexts(fileInfo, (pattern, file) => pattern.Test((file as IFileSystemFile)!));

                if (result.IsSuccessful)
                {
                    _results.Add(fileInfo);
                    //_files.Add(new GlobPatternMatch(
                    //    path: CombinePath(parentRelativePath!, fileInfo.Name),
                    //    stem: result.Stem));
                }

                continue;
            }
        }

        // Matches the sub directories recursively
        foreach (var child in children)
        {
            //string relativePath = CombinePath(parentRelativePath!, child.Name);

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

        foreach (IGlobPatternContext include in _includes)
        {
            include.Declare(DeclareInclude);
        }
    }

    private void DeclareInclude(FileSystemPathSegment patternSegment, bool isLastSegment)
    {
        var literalSegment = patternSegment as LiteralSegment;
        if (literalSegment != null)
        {
            if (isLastSegment)
            {
                _declaredLiteralFileSegments.Add(literalSegment);
            }
            else
            {
                _declaredLiteralFolderSegments.Add(literalSegment);
                _declaredLiteralFolderSegmentInString.Add(literalSegment.Value);
            }
        }
        else if (patternSegment is ParentSegment)
        {
            _declaredParentPathSegment = true;
        }
        else if (patternSegment is WildcardSegment)
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

    // Used to adapt Test(DirectoryInfoBase) for the below overload
    private bool MatchPatternContexts(IFileSystemInfo fileinfo, Func<IGlobPatternContext, IFileSystemInfo, bool> test)
    {
        return MatchPatternContexts(
            fileinfo,
            (ctx, file) =>
            {
                if (test(ctx, file))
                {
                    return GlobPatternTestResult.Success(stem: string.Empty);
                }
                else
                {
                    return GlobPatternTestResult.Failed;
                }
            }).IsSuccessful;
    }
    private GlobPatternTestResult MatchPatternContexts(IFileSystemInfo fileinfo, Func<IGlobPatternContext, IFileSystemInfo, GlobPatternTestResult> test)
    {
        GlobPatternTestResult result = GlobPatternTestResult.Failed;

        // If the given file/directory matches any including pattern, continues to next step.
        foreach (IGlobPatternContext context in _includes)
        {
            GlobPatternTestResult localResult = test(context, fileinfo);
            if (localResult.IsSuccessful)
            {
                result = localResult;
                break;
            }
        }

        // If the given file/directory doesn't match any of the including pattern, returns false.
        if (!result.IsSuccessful)
        {
            return GlobPatternTestResult.Failed;
        }

        // If the given file/directory matches any excluding pattern, returns false.
        foreach (IGlobPatternContext context in _excludes)
        {
            if (test(context, fileinfo).IsSuccessful)
            {
                return GlobPatternTestResult.Failed;
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
        foreach (IGlobPatternContext context in _includes)
        {
            context.PushDirectory(directory);
        }

        foreach (IGlobPatternContext context in _excludes)
        {
            context.PushDirectory(directory);
        }
    }
}
