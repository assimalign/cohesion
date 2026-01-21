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

    private Dictionary<string, string> _referenceCache = new Dictionary<string, string>();


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
            solutionFolderRoot = SolutionRootFolder;
        }
        else
        {
            solutionFolderParent = Directory.GetParent(solutionPath)?.FullName;
            solutionFolderRoot = solutionPath.Replace(solutionFolderParent!, "");
        }

        // Get only ending directory name
        solutionFolderName = System.IO.Path.Join(solutionFolderRoot, path.Replace(solutionPath, "")).Replace("\\", "/");

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
            .Where(p => p.Exists && p.Extension != ".slnx" && p.Extension != ".sln");

        var directories = entries
            .Select(p => new DirectoryInfo(p))
            .Where(p => p.Exists && !p.Attributes.HasFlag(FileAttributes.Hidden));

        // Check if the current directory has any known MSBuild Project Files
        var hasSolutionProject = files.Any(p => _knownSolutionProjects.Contains(p.Extension));

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
        string? solutionParentPath = Directory.GetParent(solutionPath)?.FullName;

        if (solutionParentPath is null)
        {
            return;
        }

        XmlSerializer serializer = new XmlSerializer(typeof(Project));

        using FileStream stream = File.Open(projectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Project? project = (Project)serializer.Deserialize(stream)!;

        foreach (var itemGroup in project.ItemGroups)
        {
            if (itemGroup.CohesionProjectReferences.Any())
            {
                WriteVerbose($"Found Project References: {itemGroup.CohesionProjectReferences.Count}");

                foreach (var reference in itemGroup.CohesionProjectReferences)
                {
                    // Check if the project was already found
                    if (_referenceCache.ContainsKey(reference?.Include!))
                    {
                        continue;
                    }

                    foreach (var projectFile in EnumerateProjectFiles(solutionPath))
                    {
                        var projectName = Path.GetFileNameWithoutExtension(projectFile);

                        if (projectName == reference?.Include)
                        {
                            _referenceCache[reference?.Include!] = Path.GetRelativePath(solutionPath, projectFile).Replace("\\", "/");

                            // Get Sub references. Want to identify the references of the references
                            GetReferenceTree(solutionPath, projectFile);

                            break;
                        }
                    }
                }
            }
        }
    }


    private IEnumerable<string> EnumerateProjectFiles(string path)
    {
        string? parent = Directory.GetParent(path)?.FullName;

        if (parent is not null)
        {
            var projects = Directory.EnumerateFiles(parent, $"*.csproj", new EnumerationOptions()
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5

            }).Where(file => !file.StartsWith(path)); // Exclude projects from already searched directories

            foreach (var item in projects)
            {
                yield return item;
            }

            foreach (var next in EnumerateProjectFiles(parent))
            {
                yield return next;
            }
        }
    }


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