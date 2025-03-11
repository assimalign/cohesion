namespace Assimalign.Cohesion.FileSystem.Physical.Tests;


using FileSystem.Globbing;


public class UnitTest1
{
    private readonly IFileSystem _fileSystem;

    public UnitTest1()
    {
        _fileSystem = new PhysicalFileSystem(new PhysicalFileSystemOptions()
        {
            Root = "C"
        });
    }

    [Fact]
    public void TestFileExists()
    {
        Assert.False(_fileSystem.Exist("/tests"));
        
        var directory = _fileSystem.CreateDirectory("tests");

        Assert.True(_fileSystem.Exist("/tests"));

        _fileSystem.DeleteDirectory("/tests");
    }



    [Fact]
    public void Test1()
    {
        var directory = _fileSystem.RootDirectory;
        var matcher = new GlobPatternMatcher(StringComparison.OrdinalIgnoreCase)
            .AddInclude("**/*.json");

        //var file = _fileSystem.GetFile("C:/users/chase/.Azure/PSConfig.json");

        var match = matcher.Match(directory);


    }
}
