// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Assimalign.Cohesion.FileSystem.Globbing;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests.TestUtility
{
    internal class MockFileInfo : FileInfoBase
    {
        public MockFileInfo(
            FileSystemOperationRecorder recorder,
            DirectoryInfoBase parentDirectory,
            string fullName,
            string name)
        {
            Recorder = recorder;
            FullName = fullName;
            Name = name;
        }

        public FileSystemOperationRecorder Recorder { get; }

        public override DirectoryInfoBase ParentDirectory { get; }

        public override string FullName { get; }

        public override string Name { get; }
    }
}
