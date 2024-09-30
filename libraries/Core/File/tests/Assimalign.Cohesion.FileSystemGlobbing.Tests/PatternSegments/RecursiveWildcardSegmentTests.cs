// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Assimalign.Cohesion.FileSystemGlobbing.Internal.PathSegments;
using Xunit;

namespace Assimalign.Cohesion.FileSystemGlobbing.Tests.PatternSegments
{
    public class RecursiveWildcardSegmentTests
    {
        [Fact]
        public void Match()
        {
            var pathSegment = new RecursiveWildcardSegment();
            Assert.False(pathSegment.Match("Anything"));
        }
    }
}
