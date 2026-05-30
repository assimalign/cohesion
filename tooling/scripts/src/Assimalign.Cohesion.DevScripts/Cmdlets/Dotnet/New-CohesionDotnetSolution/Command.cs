using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Xml.Serialization;

namespace Assimalign.Cohesion.DevScripts;

[Cmdlet("New", "CohesionDotnetSolution")]
[OutputType(typeof(CohesionDotnetSolution))]
public class NewCohesionDotnetSolutionCmdlet : PSCmdlet
{
    public static readonly string[] _knownSolutionProjects = new string[]
    {
        ".csproj"
    };

    private readonly Dictionary<string, string> _referenceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolvedReferences = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _projectIndex;

    // Build-output and version-control directories never contribute valid reference candidates.
    private static readonly string[] _excludedReferenceSegments =
    {
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}_out{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
    };


    public NewCohesionDotnetSolutionCmdlet() { }

    #region Parameters

    [Parameter(Mandatory = true, Position = 0, HelpMessage = "The location to create the .NET solution file.")]
    public string? SolutionPath
    {
        get => field;
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentNullException("A valid Path parameter is required.");
            }

            if (value.EndsWith(".slnx"))
            {
                SolutionName = System.IO.Path.GetFileName(value);
                field = System.IO.Path.GetDirectoryName(value);
            }
            else
            {
                field = value;
            }
        }
    }

    [Parameter(Mandatory = false, Position = 1, HelpMessage = "The name of the .NET solution file.")]
    public string? SolutionName
    {
        get => field;
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentNullException("A valid Name parameter is required.");
            }

            if (!value.EndsWith(".slnx"))
            {
                field = string.Join('.', value, "slnx");
            }
            else
            {
                field = value;
            }
        }
    }

    [Parameter(Mandatory = false, Position = 2, HelpMessage = "The name of the Root Solution Folder to add all solution items under. The default is the directory name in the '-Path' parameter.")]
    public string? SolutionRootFolder { get; set; }

    [Parameter(Mandatory = false, Position = 3, HelpMessage = "Search's outside parent directories of the provided -Path parameter for the given project references.")]
    public SwitchParameter IncludeReferences { get; set; } = false;

    [Parameter(Mandatory = false, Position = 4)]
    public string ReferenceFolderName { get; set; } = "refs";

    [Parameter(Mandatory = false, Position = 5)]
    public SwitchParameter Force { get; set; } = false;

    [Parameter(Mandatory = false, Position = 6)]
    public string[] IgnorePaths { get; set; } = [];

    [Parameter(Mandatory = false, Position = 7)]
    public CohesionDotnetSolutionGroupingInput[] Grouping { get; set; }

    #endregion

    internal string FilePath
    {
        get
        {
            // If only directory path is provided with no solution name in Name parameter or part of path
            // throw an exception
            if (string.IsNullOrEmpty(SolutionName))
            {
                throw new ArgumentException("A File name must be provided. Either provide within the -Path parameter or use the -Name parameter.");
            }

            return System.IO.Path.Join(SolutionPath, SolutionName);
        }
    }

    internal Glob[] Globs => IgnorePaths.Select(path => Glob.Parse(path)).ToArray();

    // This method gets called once for each cmdlet in the pipeline when the pipeline starts executing
    protected override void BeginProcessing()
    {
        WriteVerbose($"Creating .NET Solution File: {FilePath}");
        base.BeginProcessing();
    }


    // This method will be called for each input received from the pipeline to this cmdlet; if no input is received, this method is not called
    protected override void ProcessRecord()
    {
        try
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendTabbedLine(0, "<Solution>");
            builder.AppendTabbedLine(1, "<Configurations>");
            builder.AppendTabbedLine(2, "<Platform Name=\"Any CPU\" />");
            builder.AppendTabbedLine(2, "<Platform Name=\"x64\" />");
            builder.AppendTabbedLine(2, "<Platform Name=\"x86\" />");
            builder.AppendTabbedLine(1, "</Configurations>");

            ProcessSolutionGeneration(SolutionPath!, SolutionPath!, builder);
            ProcessReferenceTree(SolutionPath!, SolutionPath!, builder);

            builder.AppendTabbedLine(0, "</Solution>");

            // Unless '-Force' switch is specified DO NOTE override the existing solution file
            if (!Force && File.Exists(FilePath))
            {
                throw new IOException("The Solution file already exists. Use parameter '-Force' to override the existing file.");
            }

            using FileStream stream = File.Create(FilePath);

            string content = builder.ToString();

            byte[] bytes = Encoding.UTF8.GetBytes(content);

            stream.Write(bytes);
            stream.Close();

            WriteObject(new CohesionDotnetSolution()
            {
                File = new FileInfo(FilePath)
            });
        }
        catch (Exception exception)
        {
            WriteError(new ErrorRecord(exception, "Unhandled", ErrorCategory.InvalidOperation, this));
        }
    }

    // This method will be called once at the end of pipeline execution; if no input is received, this method is not called
    protected override void EndProcessing()
    {
        WriteVerbose($"Created .NET Solution File: {FilePath}");
    }


    private string GetSolutionFolderName(string path, string solutionPath)
    {
        // Get the Parent Folder 
        string? solutionFolderParent = string.Empty;
        string? solutionFolderRoot = string.Empty;
        string? solutionFolderName = string.Empty;

        // If the SolutionRootFolder was provided let's not get the parent
        if (!string.IsNullOrEmpty(SolutionRootFolder))
        {
            solutionFolderRoot = SolutionRootFolder.Trim('/').Trim('\\');
        }
        else
        {
            solutionFolderParent = Directory.GetParent(solutionPath)?.FullName;
            solutionFolderRoot = solutionPath.Replace(solutionFolderParent!, "").Trim('/').Trim('\\');
        }

        // Get only ending directory name
        solutionFolderName = System.IO.Path.Join(solutionFolderRoot, path.Replace(solutionPath, "")).Replace("\\", "/");

        if (Grouping is not null && Grouping.Any())
        {
            var group = Grouping.FirstOrDefault(grouping =>
            {
                return grouping.Paths.Any(p =>
                {
                    var value = $"{solutionFolderRoot}{p}".Trim('/').Trim('\\') + "/";

                    if (value.Length > solutionFolderName.Length + 1)
                    {
                        return false;
                    }

                   WriteVerbose($"{solutionFolderName}/ - sw -> {value}");

                    return $"{solutionFolderName}/".StartsWith(value, StringComparison.OrdinalIgnoreCase);
                });
            });

            if (group is not null)
            {
                solutionFolderName = $"/{solutionFolderRoot}/{group.Folder}" + $"/{solutionFolderName.TrimStart('/')}".Replace($"/{solutionFolderRoot}", "");
            }
        }

        // Ensure trailing slash is added
        if (!solutionFolderName.StartsWith("/"))
        {
            solutionFolderName = "/" + solutionFolderName;
        }

        // Ensure leading slash is added
        if (!solutionFolderName.EndsWith("/"))
        {
            solutionFolderName = solutionFolderName + "/";
        }

        return solutionFolderName;
    }

    private void ProcessSolutionGeneration(string path, string solutionPath, StringBuilder builder)
    {
        string solutionFolderName = GetSolutionFolderName(path, solutionPath);

        var entries = Directory.GetFileSystemEntries(path);

        var files = entries
            .Select(p => new FileInfo(p))
            .Where(p => p.Exists && p.Extension != ".slnx" && p.Extension != ".sln" && !Globs.Any(glob => glob.IsMatch(p.FullName)));

        var directories = entries
            .Select(p => new DirectoryInfo(p))
            .Where(p => p.Exists && !p.Attributes.HasFlag(FileAttributes.Hidden) && !Globs.Any(glob => glob.IsMatch(p.FullName)));

        if (!directories.Any() && !files.Any())
        {
            return;
        }

        // Check if the current directory has any known MSBuild Project Files
        bool hasSolutionProject = files.Any(p => _knownSolutionProjects.Contains(p.Extension));

        builder.AppendTabbed(1, $"<Folder Name=\"{solutionFolderName}\"");

        if (files.Any())
        {
            builder.AppendLine(">");

            if (hasSolutionProject)
            {
                var file = files.First(file => _knownSolutionProjects.Contains(file.Extension));
                var filePath = file.FullName.Replace(solutionPath, "").Replace("\\", "/").TrimStart('/');

                builder.AppendTabbedLine(2, $"<Project Path=\"{filePath}\" />");

                if (IncludeReferences)
                {
                    GetReferenceTree(solutionPath, file.FullName);
                }
            }
            else
            {
                foreach (var file in files)
                {
                    var filePath = file.FullName.Replace(solutionPath, "").Replace("\\", "/").TrimStart('/');

                    builder.AppendTabbedLine(2, $"<File Path=\"{filePath}\" />");
                }
            }

            builder.AppendTabbedLine(1, "</Folder>");
        }
        else
        {
            builder.AppendLine(" />");
        }

        if (!hasSolutionProject)
        {
            foreach (var directory in directories)
            {
                ProcessSolutionGeneration(directory.FullName, solutionPath, builder);
            }
        }
    }

    private void ProcessReferenceTree(string path, string solutionPath, StringBuilder builder)
    {
        string solutionFolderName = GetSolutionFolderName(path, solutionPath) + ReferenceFolderName + "/";

        if (_referenceCache.Any())
        {
            builder.AppendTabbedLine(1, $"<Folder Name=\"{solutionFolderName}\" >");

            foreach (var item in _referenceCache)
            {
                builder.AppendTabbedLine(2, $"<Project Path=\"{item.Value}\" />");
            }

            builder.AppendTabbedLine(1, "</Folder>");
        }
    }


    private void GetReferenceTree(string solutionPath, string projectPath)
    {
        Project project;

        try
        {
            using FileStream stream = File.Open(projectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            XmlSerializer serializer = new XmlSerializer(typeof(Project));

            project = (Project)serializer.Deserialize(stream)!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A locked, missing, or malformed project file must not abort the whole
            // solution generation - warn and keep resolving the remaining references.
            WriteWarning($"Unable to read project references from '{projectPath}': {exception.Message}");
            return;
        }

        Dictionary<string, string> projectIndex = GetProjectIndex(solutionPath);

        foreach (var itemGroup in project.ItemGroups)
        {
            foreach (var reference in itemGroup.CohesionProjectReferences)
            {
                var include = reference?.Include;

                // Ignore malformed '<CohesionProjectReference />' entries that carry no Include.
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                // Skip references already resolved, or already known to be unresolvable.
                // The negative cache is what stops the previous behavior of re-scanning
                // the disk every time for a reference that does not exist.
                if (_referenceCache.ContainsKey(include) || _unresolvedReferences.Contains(include))
                {
                    continue;
                }

                if (projectIndex.TryGetValue(include, out var referencePath))
                {
                    _referenceCache[include] = Path.GetRelativePath(solutionPath, referencePath).Replace("\\", "/");

                    WriteVerbose($"Resolved project reference '{include}' -> {_referenceCache[include]}");

                    // Resolve the references of the resolved project. The cache check
                    // above doubles as the cycle guard for circular references.
                    GetReferenceTree(solutionPath, referencePath);
                }
                else
                {
                    _unresolvedReferences.Add(include);

                    WriteVerbose($"Project reference '{include}' was not found under the search root; skipping.");
                }
            }
        }
    }


    /// <summary>
    /// Builds, once per cmdlet invocation, a lookup of candidate reference projects keyed by
    /// project name (file name without extension). Candidates are every <c>*.csproj</c> beneath
    /// the resolved search root, excluding projects that already live inside the solution
    /// directory and any in build-output or version-control directories.
    /// </summary>
    private Dictionary<string, string> GetProjectIndex(string solutionPath)
    {
        if (_projectIndex is not null)
        {
            return _projectIndex;
        }

        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string searchRoot = GetReferenceSearchRoot(solutionPath);

        // Projects inside the solution directory are emitted by the solution-generation pass,
        // so they must not be duplicated as external references.
        string solutionPrefix =
            solutionPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var options = new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true
        };

        foreach (var projectFile in Directory.EnumerateFiles(searchRoot, "*.csproj", options))
        {
            if (projectFile.StartsWith(solutionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsExcludedReferencePath(projectFile))
            {
                continue;
            }

            var projectName = Path.GetFileNameWithoutExtension(projectFile);

            // First match wins when several projects share a file name.
            if (!index.ContainsKey(projectName))
            {
                index[projectName] = projectFile;
            }
        }

        WriteVerbose($"Indexed {index.Count} candidate reference project(s) under '{searchRoot}'.");

        return _projectIndex = index;
    }


    /// <summary>
    /// Resolves the directory the reference search is bounded to. Prefers the repository root
    /// (the nearest ancestor containing a <c>.git</c> entry) so the search can never walk the
    /// entire drive; when the solution is not inside a git repository the search is bounded to
    /// the solution's immediate parent directory.
    /// </summary>
    private static string GetReferenceSearchRoot(string solutionPath)
    {
        DirectoryInfo? parent = Directory.GetParent(solutionPath);

        if (parent is null)
        {
            // The solution lives at a drive root; there is nothing above it to search.
            return solutionPath;
        }

        for (DirectoryInfo? current = parent; current is not null; current = current.Parent)
        {
            string gitPath = Path.Combine(current.FullName, ".git");

            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return current.FullName;
            }
        }

        return parent.FullName;
    }


    /// <summary>
    /// Determines whether a discovered project path lives in a build-output or version-control
    /// directory that should never contribute reference candidates.
    /// </summary>
    private static bool IsExcludedReferencePath(string path) =>
        _excludedReferenceSegments.Any(segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase));


    [XmlRoot("Project")]
    public class Project
    {
        [XmlAttribute("Sdk")]
        public string? Sdk { get; set; }

        [XmlElement("ItemGroup")]
        public List<ItemGroup> ItemGroups { get; set; } = new();

        public static Project Deserialize(string path)
        {
            using var stream = File.OpenRead(path);
            var serializer = new XmlSerializer(typeof(Project));
            return (Project)serializer.Deserialize(stream)!;
        }

        public static void Serialize(Project project, string path)
        {
            using var stream = File.Create(path);
            var serializer = new XmlSerializer(typeof(Project));
            serializer.Serialize(stream, project);
        }
    }

    public class ItemGroup
    {
        [XmlElement("CohesionProjectReference")]
        public List<CohesionProjectReference> CohesionProjectReferences { get; set; } = new();

        [XmlElement("ProjectReference")]
        public List<ProjectReference> ProjectReferences { get; set; } = new();
    }


    public class ProjectReference
    {
        [XmlAttribute("Include")]
        public string? Include { get; set; }
    }

    public class CohesionProjectReference
    {
        [XmlAttribute("Include")]
        public string? Include { get; set; }
    }




}