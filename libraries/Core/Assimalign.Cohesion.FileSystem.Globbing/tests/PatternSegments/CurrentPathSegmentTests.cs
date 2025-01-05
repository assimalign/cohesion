// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Assimalign.Cohesion.FileSystem.Globbing.Internal.PathSegments;
using Xunit;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests.PatternSegments
{
    public class CurrentPathSegmentTests
    {
        [Theory]
        [InlineData("anything")]
        [InlineData("")]
        [InlineData(null)]
        public void Match(string testSample)
        {
            var pathSegment = new CurrentPathSegment();
            Assert.False(pathSegment.Match(testSample));
        }
    }
}
