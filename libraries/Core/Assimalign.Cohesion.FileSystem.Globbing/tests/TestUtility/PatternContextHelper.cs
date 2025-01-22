// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Assimalign.Cohesion.FileSystem.Globbing.Internal;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests.TestUtility
{
    internal static class PatternContextHelper
    {
        public static void PushDirectory(IFilePatternContext context, params string[] directoryNames)
        {
            foreach (var each in directoryNames)
            {
                var directory = new MockDirectoryInfo(null, null, string.Empty, each, null);

                context.PushDirectory(directory);
            }
        }
    }
}
