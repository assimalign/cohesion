namespace Assimalign.PanopticDb.Storage.Tests
{
    public class UnitTest1
    {
        [Fact]
        public void Test1()
        {
            var storage = default(IStorage);
            var iterator = storage.GetIterator();

            while (iterator.MoveNext())
            {
               var segment = iterator.Current;

                segment.

            }
        }
    }
}