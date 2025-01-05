
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Globbing;


public static class FileMatcherExtensions
{
    /// <summary>
    /// Adds multiple exclude patterns to <see cref="FilePatternMatcher" />. <seealso cref="FilePatternMatcher.AddExclude(string)" />
    /// </summary>
    /// <param name="matcher">The matcher to which the exclude patterns are added</param>
    /// <param name="excludePatternsGroups">A list of globbing patterns</param>
    public static void AddExcludePatterns(this FilePatternMatcher matcher, params IEnumerable<string>[] excludePatternsGroups)
    {
        foreach (IEnumerable<string> group in excludePatternsGroups)
        {
            foreach (string pattern in group)
            {
                matcher.AddExclude(pattern);
            }
        }
    }

    /// <summary>
    /// Adds multiple patterns to include in <see cref="FilePatternMatcher" />. See <seealso cref="FilePatternMatcher.AddInclude(string)" />
    /// </summary>
    /// <param name="matcher">The matcher to which the include patterns are added</param>
    /// <param name="includePatternsGroups">A list of globbing patterns</param>
    public static void AddIncludePatterns(this FilePatternMatcher matcher, params IEnumerable<string>[] includePatternsGroups)
    {
        foreach (IEnumerable<string> group in includePatternsGroups)
        {
            foreach (string pattern in group)
            {
                matcher.AddInclude(pattern);
            }
        }
    }

    /// <summary>
    /// Searches the directory specified for all files matching patterns added to this instance of <see cref="FilePatternMatcher" />
    /// </summary>
    /// <param name="matcher">The matcher</param>
    /// <param name="directoryPath">The root directory for the search</param>
    /// <returns>Absolute file paths of all files matched. Empty enumerable if no files matched given patterns.</returns>
    //public static IEnumerable<string> GetResultsInFullPath(this FilePatternMatcher matcher, string directoryPath)
    //{
    //    IEnumerable<FilePatternMatch> matches = matcher.Execute(new FileDirectoryInfo(new DirectoryInfo(directoryPath))).Files;
    //    string[] result = matches.Select(match => Path.GetFullPath(Path.Combine(directoryPath, match.Path))).ToArray();

    //    return result;
    //}

    /// <summary>
    /// Matches the file passed in with the patterns in the matcher without going to disk.
    /// </summary>
    /// <param name="matcher">The matcher that holds the patterns and pattern matching type.</param>
    /// <param name="file">The file to run the matcher against.</param>
    /// <returns>The match results.</returns>
    //public static FilePatternMatchingResult Match(this FilePatternMatcher matcher, string file)
    //{
    //    return Match(matcher, Directory.GetCurrentDirectory(), new List<string> { file });
    //}

    /// <summary>
    /// Matches the file passed in with the patterns in the matcher without going to disk.
    /// </summary>
    /// <param name="matcher">The matcher that holds the patterns and pattern matching type.</param>
    /// <param name="rootDir">The root directory for the matcher to match the file from.</param>
    /// <param name="file">The file to run the matcher against.</param>
    /// <returns>The match results.</returns>
    //public static FilePatternMatchingResult Match(this FilePatternMatcher matcher, string rootDir, string file)
    //{
    //    return Match(matcher, rootDir, new List<string> { file });
    //}

    /// <summary>
    /// Matches the files passed in with the patterns in the matcher without going to disk.
    /// </summary>
    /// <param name="matcher">The matcher that holds the patterns and pattern matching type.</param>
    /// <param name="files">The files to run the matcher against.</param>
    /// <returns>The match results.</returns>
    //public static FilePatternMatchingResult Match(this FilePatternMatcher matcher, IEnumerable<string> files)
    //{
    //    return Match(matcher, Directory.GetCurrentDirectory(), files);
    //}

    /// <summary>
    /// Matches the files passed in with the patterns in the matcher without going to disk.
    /// </summary>
    /// <param name="matcher">The matcher that holds the patterns and pattern matching type.</param>
    /// <param name="rootDir">The root directory for the matcher to match the files from.</param>
    /// <param name="files">The files to run the matcher against.</param>
    /// <returns>The match results.</returns>
    //public static FilePatternMatchingResult Match(this FilePatternMatcher matcher, string rootDir, IEnumerable<string> files)
    //{
    //    if (matcher == null)
    //    {
    //        throw new ArgumentNullException(nameof(matcher));
    //    }

    //    return matcher.Execute(new InMemoryFileDirectoryInfo(rootDir, files));
    //}
}
