using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing;

using Assimalign.Cohesion.FileSystem.Globbing.Internal;



/// <summary>
/// Searches the file system for files with names that match specified patterns.
/// </summary>
/// <remarks>
///     <para>
///     Patterns specified in <seealso cref="AddInclude(string)" /> and <seealso cref="AddExclude(string)" /> can use
///     the following formats to match multiple files or directories.
///     </para>
///     <list type="bullet">
///         <item>
///             <term>
///             exact directory and file name
///             </term>
///             <description>
///                 <list type="bullet">
///                     <item>
///                         <term>"one.txt"</term>
///                     </item>
///                     <item>
///                         <term>"dir/two.txt"</term>
///                     </item>
///                 </list>
///             </description>
///         </item>
///         <item>
///             <term>
///             wildcards (*) in file and directory names that represent zero to many characters not including
///             directory separators characters
///             </term>
///             <description>
///                 <list type="bullet">
///                 <item>
///                     <term>"*.txt"</term><description>all files with .txt file extension</description>
///                 </item>
///                 <item>
///                     <term>"*.*"</term><description>all files with an extension</description>
///                 </item>
///                 <item>
///                     <term>"*"</term><description>all files in top level directory</description>
///                 </item>
///                 <item>
///                     <term>".*"</term><description>filenames beginning with '.'</description>
///                 </item>
///                 - "*word* - all files with 'word' in the filename
///                 <item>
///                     <term>"readme.*"</term>
///                     <description>all files named 'readme' with any file extension</description>
///                 </item>
///                 <item>
///                     <term>"styles/*.css"</term>
///                     <description>all files with extension '.css' in the directory 'styles/'</description>
///                 </item>
///                 <item>
///                     <term>"scripts/*/*"</term>
///                     <description>all files in 'scripts/' or one level of subdirectory under 'scripts/'</description>
///                 </item>
///                 <item>
///                     <term>"images*/*"</term>
///                     <description>all files in a folder with name that is or begins with 'images'</description>
///                 </item>
///                 </list>
///             </description>
///         </item>
///         <item>
///             <term>arbitrary directory depth ("/**/")</term>
///             <description>
///                 <list type="bullet">
///                     <item>
///                         <term>"**/*"</term><description>all files in any subdirectory</description>
///                     </item>
///                     <item>
///                         <term>"dir/**/*"</term><description>all files in any subdirectory under 'dir/'</description>
///                     </item>
///                 </list>
///             </description>
///         </item>
///         <item>
///             <term>relative paths</term>
///             <description>
///             '../shared/*' - all files in a diretory named 'shared' at the sibling level to the base directory given
///             to <see cref="Execute(FileDirectoryInfo)" />
///             </description>
///         </item>
///     </list>
/// </remarks>
public class FilePatternMatcher
{
    private readonly IList<IFilePattern> _includePatterns = new List<IFilePattern>();
    private readonly IList<IFilePattern> _excludePatterns = new List<IFilePattern>();
    private readonly FilePatternBuilder _builder;
    private readonly StringComparison _comparison;

    /// <summary>
    /// Initializes an instance of <see cref="FilePatternMatcher" /> using case-insensitive matching
    /// </summary>
    public FilePatternMatcher() : this(StringComparison.OrdinalIgnoreCase)
    {
    }

    /// <summary>
    /// Initializes an instance of <see cref="FilePatternMatcher" /> using the string comparison method specified
    /// </summary>
    /// <param name="comparisonType">The <see cref="StringComparison" /> to use</param>
    public FilePatternMatcher(StringComparison comparisonType)
    {
        _comparison = comparisonType;
        _builder = new FilePatternBuilder(comparisonType);
    }

    /// <summary>
    ///     <para>
    ///     Add a file name pattern that the matcher should use to discover files. Patterns are relative to the root
    ///     directory given when <see cref="Execute(FileDirectoryInfo)" /> is called.
    ///     </para>
    ///     <para>
    ///     Use the forward slash '/' to represent directory separator. Use '*' to represent wildcards in file and
    ///     directory names. Use '**' to represent arbitrary directory depth. Use '..' to represent a parent directory.
    ///     </para>
    /// </summary>
    /// <param name="pattern">The globbing pattern</param>
    /// <returns>The matcher</returns>
    public virtual FilePatternMatcher AddInclude(string pattern)
    {
        _includePatterns.Add(_builder.Build(pattern));
        return this;
    }

    /// <summary>
    ///     <para>
    ///     Add a file name pattern for files the matcher should exclude from the results. Patterns are relative to the
    ///     root directory given when <see cref="Execute(FileDirectoryInfo)" /> is called.
    ///     </para>
    ///     <para>
    ///     Use the forward slash '/' to represent directory separator. Use '*' to represent wildcards in file and
    ///     directory names. Use '**' to represent arbitrary directory depth. Use '..' to represent a parent directory.
    ///     </para>
    /// </summary>
    /// <param name="pattern">The globbing pattern</param>
    /// <returns>The matcher</returns>
    public virtual FilePatternMatcher AddExclude(string pattern)
    {
        _excludePatterns.Add(_builder.Build(pattern));
        return this;
    }

    /// <summary>
    /// Searches the directory specified for all files matching patterns added to this instance of <see cref="FilePatternMatcher" />
    /// </summary>
    /// <param name="directory">The root directory for the search</param>
    /// <returns>Always returns instance of <see cref="FilePatternMatchingResult" />, even if not files were matched</returns>
    public virtual FilePatternMatchingResult Execute(IFileSystemDirectory directory)
    {
        var context = new FileMatcherContext(_includePatterns, _excludePatterns, directory, _comparison);
        return context.Execute();
    }
}
