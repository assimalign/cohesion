using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing;

using Assimalign.Cohesion.FileSystem.Globbing.PathSegments;
using Assimalign.Cohesion.FileSystem.Globbing.Internal.Utilities;

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
public class FileMatcherContext
{
    private readonly IFileSystemDirectory _root;
    private readonly List<IFilePatternContext> _includePatternContexts;
    private readonly List<IFilePatternContext> _excludePatternContexts;
    private readonly List<FilePatternMatch> _files;

    private readonly HashSet<string> _declaredLiteralFolderSegmentInString;
    private readonly HashSet<FilePathLiteralSegment> _declaredLiteralFolderSegments = new HashSet<FilePathLiteralSegment>();
    private readonly HashSet<FilePathLiteralSegment> _declaredLiteralFileSegments = new HashSet<FilePathLiteralSegment>();

    private bool _declaredParentPathSegment;
    private bool _declaredWildcardPathSegment;

    private readonly StringComparison _comparisonType;

    public FileMatcherContext(
        IEnumerable<IFilePattern> includePatterns,
        IEnumerable<IFilePattern> excludePatterns,
        IFileSystemDirectory container,
        StringComparison comparison)
    {
        _root = container;
        _files = new List<FilePatternMatch>();
        _comparisonType = comparison;
        _includePatternContexts = includePatterns.Select(pattern => pattern.CreatePatternContextForInclude()).ToList();
        _excludePatternContexts = excludePatterns.Select(pattern => pattern.CreatePatternContextForExclude()).ToList();
        _declaredLiteralFolderSegmentInString = new HashSet<string>(StringComparisonHelper.GetStringComparer(comparison));
    }

    public FilePatternMatchingResult Execute()
    {
        _files.Clear();

        Match(_root, parentRelativePath: null);

        return new FilePatternMatchingResult(_files, _files.Count > 0);
    }

    private void Match(IFileSystemDirectory directory, string parentRelativePath)
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
                if (_declaredLiteralFolderSegmentInString.Contains(candidate.Name))
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
        var subDirectories = new List<IFileSystemDirectory>();

        foreach (var fileInfo in entities)
        {
            if (fileInfo is IFileSystemDirectory directoryInfo)
            {
                if (MatchPatternContexts(directoryInfo, (pattern, dir) => pattern.Test(dir)))
                {
                    subDirectories.Add(directoryInfo);
                }

                continue;
            }
            else 
            {
                FilePatternTestResult result = MatchPatternContexts(fileInfo, (pattern, file) => pattern.Test(file));
                
                if (result.IsSuccessful)
                {
                    _files.Add(new FilePatternMatch(
                        path: CombinePath(parentRelativePath, fileInfo.Name),
                        stem: result.Stem));
                }

                continue;
            }
        }

        // Matches the sub directories recursively
        foreach (var subDir in subDirectories)
        {
            string relativePath = CombinePath(parentRelativePath, subDir.Name);

            Match(subDir, relativePath);
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

        foreach (IFilePatternContext include in _includePatternContexts)
        {
            include.Declare(DeclareInclude);
        }
    }

    private void DeclareInclude(IFilePathSegment patternSegment, bool isLastSegment)
    {
        var literalSegment = patternSegment as FilePathLiteralSegment;
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
        else if (patternSegment is FilePathParentSegment)
        {
            _declaredParentPathSegment = true;
        }
        else if (patternSegment is FilePathWildcardSegment)
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
    private bool MatchPatternContexts<TFileInfoBase>(TFileInfoBase fileinfo, Func<IFilePatternContext, TFileInfoBase, bool> test)
    {
        return MatchPatternContexts(
            fileinfo,
            (ctx, file) =>
            {
                if (test(ctx, file))
                {
                    return FilePatternTestResult.Success(stem: string.Empty);
                }
                else
                {
                    return FilePatternTestResult.Failed;
                }
            }).IsSuccessful;
    }
    private FilePatternTestResult MatchPatternContexts<TFileInfoBase>(TFileInfoBase fileinfo, Func<IFilePatternContext, TFileInfoBase, FilePatternTestResult> test)
    {
        FilePatternTestResult result = FilePatternTestResult.Failed;

        // If the given file/directory matches any including pattern, continues to next step.
        foreach (IFilePatternContext context in _includePatternContexts)
        {
            FilePatternTestResult localResult = test(context, fileinfo);
            if (localResult.IsSuccessful)
            {
                result = localResult;
                break;
            }
        }

        // If the given file/directory doesn't match any of the including pattern, returns false.
        if (!result.IsSuccessful)
        {
            return FilePatternTestResult.Failed;
        }

        // If the given file/directory matches any excluding pattern, returns false.
        foreach (IFilePatternContext context in _excludePatternContexts)
        {
            if (test(context, fileinfo).IsSuccessful)
            {
                return FilePatternTestResult.Failed;
            }
        }

        return result;
    }
    private void PopDirectory()
    {
        foreach (IFilePatternContext context in _excludePatternContexts)
        {
            context.PopDirectory();
        }

        foreach (IFilePatternContext context in _includePatternContexts)
        {
            context.PopDirectory();
        }
    }
    private void PushDirectory(IFileSystemDirectory directory)
    {
        foreach (IFilePatternContext context in _includePatternContexts)
        {
            context.PushDirectory(directory);
        }

        foreach (IFilePatternContext context in _excludePatternContexts)
        {
            context.PushDirectory(directory);
        }
    }
}
