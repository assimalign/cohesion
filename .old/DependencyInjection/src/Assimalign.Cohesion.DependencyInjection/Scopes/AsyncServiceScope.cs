using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.DependencyInjection;


/// <summary>
/// An <see cref="IServiceScope" /> implementation that implements <see cref="IAsyncDisposable" />.
/// </summary>
public readonly struct AsyncServiceScope : IServiceScope, IAsyncDisposable
{
    private readonly IServiceScope serviceScope;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncServiceScope"/> struct.
    /// Wraps an instance of <see cref="IServiceScope" />.
    /// </summary>
    /// <param name="serviceScope">The <see cref="IServiceScope"/> instance to wrap.</param>
    public AsyncServiceScope(IServiceScope serviceScope) => this.serviceScope = serviceScope ?? throw new ArgumentNullException(nameof(serviceScope));

    /// <inheritdoc />
    public IServiceProvider ServiceProvider => this.serviceScope.ServiceProvider;

    /// <inheritdoc />
    public void Dispose() => this.serviceScope.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (this.serviceScope is IAsyncDisposable ad)
        {
            return ad.DisposeAsync();
        }
        this.serviceScope.Dispose();

        // ValueTask.CompletedTask is only available in net5.0 and later.
        return ValueTask.CompletedTask;
    }
}
