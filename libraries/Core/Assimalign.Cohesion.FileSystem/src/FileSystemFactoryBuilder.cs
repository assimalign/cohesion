using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.FileSystem;

public class FileSystemFactoryBuilder
{
    private readonly ConcurrentDictionary<string, IFileSystem> _fileSystems;

    public FileSystemFactoryBuilder()
    {
        _fileSystems = new ConcurrentDictionary<string, IFileSystem>();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    /// <param name="fileSystem"></param>
    /// <returns></returns>
    public FileSystemFactoryBuilder AddFileSystem(string name, IFileSystem fileSystem)
    {
        _fileSystems.AddOrUpdate(name, (key) => fileSystem, (key, value) => fileSystem);

        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TFileSystem"></typeparam>
    /// <param name="func"></param>
    /// <returns></returns>
    public FileSystemFactoryBuilder AddFileSystem<TFileSystem>(Func<TFileSystem> func)
        where TFileSystem : IFileSystem
    {
        var name = typeof(TFileSystem).Name;

        _fileSystems.AddOrUpdate(name, (key) => func.Invoke(), (key, value) => func.Invoke());

        return this;
    }


    public IFileSystemFactory Build()
    {
        return new FileSystemFactory(_fileSystems);
    }

    partial class FileSystemFactory : IFileSystemFactory
    {
        private readonly ConcurrentDictionary<string, IFileSystem> _fileSystems;

        public FileSystemFactory(ConcurrentDictionary<string, IFileSystem> fileSystems)
        {
            _fileSystems = fileSystems;
        }


        public IFileSystem Create(string name)
        {
            return _fileSystems[name];
        }

        public IFileSystem Create<TFileSystem>() where TFileSystem : IFileSystem
        {
            throw new NotImplementedException();
        }
    }
}
