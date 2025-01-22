namespace Assimalign.Cohesion.FileSystem.Physical.Tests;



public class UnitTest1
{
    private readonly IFileSystem fileSystem;

    public UnitTest1()
    {
        this.fileSystem = new PhysicalFileSystem("C");
    }

    [Fact]
    public void TestFileExists()
    {
        Assert.False(fileSystem.Exist("/tests"));
        
        var directory = fileSystem.CreateDirectory("tests");

        Assert.True(fileSystem.Exist("/tests"));

        fileSystem.DeleteDirectory("/tests");
    }




    



    [Fact]
    public void Test1()
    {
        IFileSystem fileSystem = new PhysicalFileSystem("C");


        fileSystem.CreateDirectory("/Test");

    }
}
