


```csharp

public interface IConfigurationRoot : IConfiguration, IDisposable, IAsyncDisposable
{
    IEnumerable<IConfigurationProvider> Providers { get; }
}

```

<video controls src="${src}" title="${title}"></video>