using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.RateLimiting;

public class Class1
{
    public Class1()
    {
        
    }

    public async Task TesT()
    {
        var limiter = new System.Threading.RateLimiting.ConcurrencyLimiter(new System.Threading.RateLimiting.ConcurrencyLimiterOptions()
        {
            
        });

        using var lease = await limiter.AcquireAsync();

        lease.
    }
}
