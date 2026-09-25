using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Files.Bmff;

namespace Assimalign.Cohesion.MediaFile.Bmff.Tests
{
    public class UnitTest1
    {
        private List<BmffBox> _boxes = new();

        [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Read the ISO-BMFF fixture",
            Skip = "requires a local media fixture: an ISO-BMFF/MP4 file at a developer-local path")]
        public void Test1()
        {
            using var stream = File.OpenRead(@"C:\Users\c.crawford\OneDrive\Videos\TV Series\Anime\Psycho Pass [Finished]\Psycho-Pass Season 02.1 Episode 01.mp4");
            var reader = BmffReader.Create(stream);

            while (reader.Read())
            {
                _boxes.Add(reader.Current);

                //if (reader.Current is BmffBoxComposite composite)
                //{
                //    Traverse(composite);
                //}
            }
        }

#pragma warning disable IDE0011 // Deviates from the repo braces rule: H2 requires the legacy fixture helper body unchanged.
        private void Traverse(BmffBoxComposite composite)
        {
            if (composite.Children is null)
                return;
            foreach (var child in composite.Children)
            {
                _boxes.Add(child);

                if (child is BmffBoxComposite composite1)
                {
                    Traverse(composite1);
                }
            }
        }
#pragma warning restore IDE0011
    }
}