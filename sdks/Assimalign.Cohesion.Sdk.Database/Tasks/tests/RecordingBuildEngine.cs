using System;
using System.Collections.Generic;

using Microsoft.Build.Framework;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

internal sealed class RecordingBuildEngine : IBuildEngine
{
    public List<BuildErrorEventArgs> Errors { get; } = [];

    public bool ContinueOnError => false;

    public int LineNumberOfTaskNode => 0;

    public int ColumnNumberOfTaskNode => 0;

    public string ProjectFileOfTaskNode => string.Empty;

    public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);

    public void LogWarningEvent(BuildWarningEventArgs e)
    {
    }

    public void LogMessageEvent(BuildMessageEventArgs e)
    {
    }

    public void LogCustomEvent(CustomBuildEventArgs e)
    {
    }

    public bool BuildProjectFile(
        string projectFileName,
        string[] targetNames,
        System.Collections.IDictionary globalProperties,
        System.Collections.IDictionary targetOutputs)
        => throw new NotSupportedException();
}
