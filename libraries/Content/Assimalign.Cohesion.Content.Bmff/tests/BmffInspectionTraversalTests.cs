using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public sealed class BmffInspectionTraversalTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: enter and leave preserve paths and document order")]
    public void Walk_Document_ReportsOrderedEnterAndLeavePaths()
    {
        // Arrange: synthetic boxes use the local header writer, including an unregistered type.
        using var source = new MemoryStream(BmffFixtures.Join(
            BmffFixtures.Box("moov", BmffFixtures.Box("trak", BmffFixtures.Box("zzzz"))), BmffFixtures.Box("free")));
        using var document = BmffDocument.Load(source);
        var events = new List<string>();

        // Act
        var completed = BmffBoxWalker.Walk(document,
            (box, depth, path) => { events.Add($"+{depth}:{string.Join('/', path)}"); return BmffWalkAction.Continue; },
            (box, depth, path) => { events.Add($"-{depth}:{string.Join('/', path)}"); return BmffWalkAction.Continue; });

        // Assert
        completed.ShouldBeTrue();
        events.ShouldBe(new[] { "+0:moov", "+1:moov/trak", "+2:moov/trak/zzzz", "-2:moov/trak/zzzz",
            "-1:moov/trak", "-0:moov", "+0:free", "-0:free" });
        document.First(FourCC.Parse("zzzz")).ShouldBeOfType<UnknownBox>();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: skip pairs leave and stop is immediate")]
    public void Walk_SkipAndStop_ObservesCallbackContract()
    {
        // Arrange
        var movie = new MovieBox(0);
        var track = new TrackBox(0, 0);
        track.Children.Add(track); // A skipped subtree must not even inspect this cycle.
        movie.Children.Add(track);
        movie.Children.Add(new FreeSpaceBox(0, 0));
        var events = new List<string>();

        // Act
        var completed = BmffBoxWalker.Walk(movie,
            (box, depth, path) =>
            {
                events.Add("+" + box.BoxType.ToFourCC());
                return box is TrackBox ? BmffWalkAction.SkipSubtree : BmffWalkAction.Continue;
            },
            (box, depth, path) => { events.Add("-" + box.BoxType.ToFourCC()); return BmffWalkAction.Stop; });

        // Assert
        completed.ShouldBeFalse();
        events.ShouldBe(new[] { "+moov", "+trak", "-trak" });
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: early stop never enters descendants")]
    public void Walk_StopOnEnter_DoesNotInspectChildrenOrCallLeave()
    {
        // Arrange
        var movie = new MovieBox(0);
        movie.Children.Add(movie);
        var leaves = 0;

        // Act
        var completed = BmffBoxWalker.Walk(movie, (_, _, _) => BmffWalkAction.Stop,
            (_, _, _) => { leaves++; return BmffWalkAction.Continue; });

        // Assert
        completed.ShouldBeFalse();
        leaves.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: deep trees use explicit frames")]
    public void Walk_TenThousandLevels_DoesNotUseTheCallStack()
    {
        // Arrange
        var root = new MovieBox(0);
        var current = root;
        for (var index = 0; index < 10000; index++)
        {
            var next = new MovieBox(index);
            current.Children.Add(next);
            current = next;
        }
        var count = 0;

        // Act
        BmffBoxWalker.Walk(root, (_, depth, path) =>
        {
            path.Count.ShouldBe(depth + 1);
            count++;
            return BmffWalkAction.Continue;
        }, maxDepth: 10000);

        // Assert
        count.ShouldBe(10001);
        root.Descendants<MovieBox>(10000).Count().ShouldBe(10000);
        Should.Throw<ContentFormatException>(() => BmffBoxWalker.Walk(root, (_, _, _) => BmffWalkAction.Continue))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: invalid trees fail with content errors")]
    public void Walk_CyclicOrNullNode_ReportsInvalidStructure()
    {
        // Arrange
        var root = new MovieBox(12);
        root.Children.Add(root);

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => BmffBoxWalker.Walk(root, (_, _, _) => BmffWalkAction.Continue));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(12);
        root.Children[0] = null!;
        Should.Throw<ContentFormatException>(() => root.Descendants<BmffBox>().ToArray())
            .ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: typed queries distinguish direct children and descendants")]
    public void Queries_NestedTree_ReturnPreorderMatchesAndHandleAbsence()
    {
        // Arrange
        using var document = new BmffDocument();
        var movie = new MovieBox(0);
        var track = new TrackBox(0, 0);
        var nested = new FreeSpaceBox(0, 0);
        var direct = new FreeSpaceBox(0, 0);
        track.Children.Add(nested);
        movie.Children.Add(track);
        movie.Children.Add(direct);
        document.Boxes.Add(movie);

        // Act / Assert
        movie.Children<FreeSpaceBox>().ShouldBe(new[] { direct });
        movie.Descendants<FreeSpaceBox>().ShouldBe(new[] { nested, direct });
        document.Children<MovieBox>().ShouldBe(new[] { movie });
        document.Descendants<MovieBox>().ShouldBe(new[] { movie });
        movie.Descendants<MovieBox>().ShouldBeEmpty();
        movie.First<FreeSpaceBox>().ShouldBeSameAs(nested);
        document.FirstOrDefault<FreeSpaceBox>().ShouldBeSameAs(nested);
        document.FindAll(FourCC.Parse("free")).ShouldBe(new[] { nested, direct });
        movie.FindAll(BmffBoxType.FreeSpace).ShouldBe(new[] { nested, direct });
        document.First(BmffBoxType.FreeSpace).ShouldBeSameAs(nested);
        movie.First(FourCC.Parse("free")).ShouldBeSameAs(nested);
        movie.FirstOrDefault(BmffBoxType.FreeSpace).ShouldBeSameAs(nested);
        document.FirstOrDefault(FourCC.Parse("xxxx")).ShouldBeNull();
        movie.FirstOrDefault<MovieHeaderBox>().ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => document.First<MovieHeaderBox>());
        Should.Throw<InvalidOperationException>(() => movie.First(FourCC.Parse("xxxx")));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: first queries leave later hostile trees untouched")]
    public void First_LaterCycle_StopsAfterFirstMatch()
    {
        // Arrange
        using var document = new BmffDocument();
        var first = new FreeSpaceBox(0, 0);
        var cycle = new MovieBox(0);
        cycle.Children.Add(cycle);
        document.Boxes.Add(first);
        document.Boxes.Add(cycle);

        // Act / Assert
        document.First<FreeSpaceBox>().ShouldBeSameAs(first);
        Should.Throw<ContentFormatException>(() => document.FindAll(BmffBoxType.FreeSpace).ToArray())
            .ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: walker and queries leave 40 GiB media lazy")]
    public void Walk_VirtualMedia_ReadsNoPayload()
    {
        // Arrange
        using var source = new SparseMediaStream();
        using var document = BmffDocument.Load(source);
        var reads = source.BytesRead;

        // Act
        BmffBoxWalker.Walk(document, (_, _, _) => BmffWalkAction.Continue);
        var media = document.First<MediaDataBox>();

        // Assert
        media.Payload.Length.ShouldBe(SparseMediaStream.MediaSize);
        source.BytesRead.ShouldBe(reads);
    }
}
