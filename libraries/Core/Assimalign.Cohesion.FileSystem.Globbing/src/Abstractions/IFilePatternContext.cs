﻿using System;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
public interface IFilePatternContext
{
    void Declare(Action<IFilePathSegment, bool> onDeclare);

    bool Test(IFileSystemDirectory directory);

    FilePatternTestResult Test(IFileSystemFile file);

    void PushDirectory(IFileSystemDirectory directory);

    void PopDirectory();
}
