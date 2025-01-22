namespace Assimalign.Cohesion.MediaFile.Bmff.Tests
{
    public class UnitTest1
    {
        private List<BmffBox> boxes = new();

        [Fact]
        public void Test1()
        {
            using var stream = File.OpenRead(@"C:\Users\c.crawford\OneDrive\Videos\TV Series\Anime\Psycho Pass [Finished]\Psycho-Pass Season 02.1 Episode 01.mp4");
            var reader = BmffReader.Create(stream);

            while (reader.Read())
            {
                boxes.Add(reader.Current);

                //if (reader.Current is BmffBoxComposite composite)
                //{
                //    Traverse(composite);
                //}
            }
        }

        private void Traverse(BmffBoxComposite composite)
        {
            if (composite.Children is null)
                return;
            foreach (var child in composite.Children)
            {
                boxes.Add(child);

                if (child is BmffBoxComposite composite1)
                {
                    Traverse(composite1);
                }
            }
        }
    }
}