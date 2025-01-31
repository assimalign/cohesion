// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Assimalign.Cohesion.FileSystem.Globbing.Internal;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests.PatternContexts
{
    internal class MockRecursivePathSegment : IFilePathSegment
    {
        public bool CanProduceStem {  get { return false; } }

        public bool Match(string value)
        {
            return false;
        }
    }
}
