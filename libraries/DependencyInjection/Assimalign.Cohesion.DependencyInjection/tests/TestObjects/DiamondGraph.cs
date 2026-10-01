namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// A 33-layer dependency graph in which both services of every layer depend on both services
/// of the next. It has 66 services but 2^32 paths from the top layer to the bottom, so a
/// validator that walks every path instead of every service never finishes.
/// </summary>
internal static class DiamondGraph
{
    /// <summary>
    /// Registers every service of the graph as a transient.
    /// </summary>
    internal static void AddTransients(IServiceProviderBuilder builder)
    {
        builder.AddTransient<Layer0A>();
        builder.AddTransient<Layer0B>();
        builder.AddTransient<Layer1A>();
        builder.AddTransient<Layer1B>();
        builder.AddTransient<Layer2A>();
        builder.AddTransient<Layer2B>();
        builder.AddTransient<Layer3A>();
        builder.AddTransient<Layer3B>();
        builder.AddTransient<Layer4A>();
        builder.AddTransient<Layer4B>();
        builder.AddTransient<Layer5A>();
        builder.AddTransient<Layer5B>();
        builder.AddTransient<Layer6A>();
        builder.AddTransient<Layer6B>();
        builder.AddTransient<Layer7A>();
        builder.AddTransient<Layer7B>();
        builder.AddTransient<Layer8A>();
        builder.AddTransient<Layer8B>();
        builder.AddTransient<Layer9A>();
        builder.AddTransient<Layer9B>();
        builder.AddTransient<Layer10A>();
        builder.AddTransient<Layer10B>();
        builder.AddTransient<Layer11A>();
        builder.AddTransient<Layer11B>();
        builder.AddTransient<Layer12A>();
        builder.AddTransient<Layer12B>();
        builder.AddTransient<Layer13A>();
        builder.AddTransient<Layer13B>();
        builder.AddTransient<Layer14A>();
        builder.AddTransient<Layer14B>();
        builder.AddTransient<Layer15A>();
        builder.AddTransient<Layer15B>();
        builder.AddTransient<Layer16A>();
        builder.AddTransient<Layer16B>();
        builder.AddTransient<Layer17A>();
        builder.AddTransient<Layer17B>();
        builder.AddTransient<Layer18A>();
        builder.AddTransient<Layer18B>();
        builder.AddTransient<Layer19A>();
        builder.AddTransient<Layer19B>();
        builder.AddTransient<Layer20A>();
        builder.AddTransient<Layer20B>();
        builder.AddTransient<Layer21A>();
        builder.AddTransient<Layer21B>();
        builder.AddTransient<Layer22A>();
        builder.AddTransient<Layer22B>();
        builder.AddTransient<Layer23A>();
        builder.AddTransient<Layer23B>();
        builder.AddTransient<Layer24A>();
        builder.AddTransient<Layer24B>();
        builder.AddTransient<Layer25A>();
        builder.AddTransient<Layer25B>();
        builder.AddTransient<Layer26A>();
        builder.AddTransient<Layer26B>();
        builder.AddTransient<Layer27A>();
        builder.AddTransient<Layer27B>();
        builder.AddTransient<Layer28A>();
        builder.AddTransient<Layer28B>();
        builder.AddTransient<Layer29A>();
        builder.AddTransient<Layer29B>();
        builder.AddTransient<Layer30A>();
        builder.AddTransient<Layer30B>();
        builder.AddTransient<Layer31A>();
        builder.AddTransient<Layer31B>();
        builder.AddTransient<Layer32A>();
        builder.AddTransient<Layer32B>();
    }

    internal sealed class Layer0A
    {
        public Layer0A(Layer1A a, Layer1B b)
        {
        }
    }

    internal sealed class Layer0B
    {
        public Layer0B(Layer1A a, Layer1B b)
        {
        }
    }

    internal sealed class Layer1A
    {
        public Layer1A(Layer2A a, Layer2B b)
        {
        }
    }

    internal sealed class Layer1B
    {
        public Layer1B(Layer2A a, Layer2B b)
        {
        }
    }

    internal sealed class Layer2A
    {
        public Layer2A(Layer3A a, Layer3B b)
        {
        }
    }

    internal sealed class Layer2B
    {
        public Layer2B(Layer3A a, Layer3B b)
        {
        }
    }

    internal sealed class Layer3A
    {
        public Layer3A(Layer4A a, Layer4B b)
        {
        }
    }

    internal sealed class Layer3B
    {
        public Layer3B(Layer4A a, Layer4B b)
        {
        }
    }

    internal sealed class Layer4A
    {
        public Layer4A(Layer5A a, Layer5B b)
        {
        }
    }

    internal sealed class Layer4B
    {
        public Layer4B(Layer5A a, Layer5B b)
        {
        }
    }

    internal sealed class Layer5A
    {
        public Layer5A(Layer6A a, Layer6B b)
        {
        }
    }

    internal sealed class Layer5B
    {
        public Layer5B(Layer6A a, Layer6B b)
        {
        }
    }

    internal sealed class Layer6A
    {
        public Layer6A(Layer7A a, Layer7B b)
        {
        }
    }

    internal sealed class Layer6B
    {
        public Layer6B(Layer7A a, Layer7B b)
        {
        }
    }

    internal sealed class Layer7A
    {
        public Layer7A(Layer8A a, Layer8B b)
        {
        }
    }

    internal sealed class Layer7B
    {
        public Layer7B(Layer8A a, Layer8B b)
        {
        }
    }

    internal sealed class Layer8A
    {
        public Layer8A(Layer9A a, Layer9B b)
        {
        }
    }

    internal sealed class Layer8B
    {
        public Layer8B(Layer9A a, Layer9B b)
        {
        }
    }

    internal sealed class Layer9A
    {
        public Layer9A(Layer10A a, Layer10B b)
        {
        }
    }

    internal sealed class Layer9B
    {
        public Layer9B(Layer10A a, Layer10B b)
        {
        }
    }

    internal sealed class Layer10A
    {
        public Layer10A(Layer11A a, Layer11B b)
        {
        }
    }

    internal sealed class Layer10B
    {
        public Layer10B(Layer11A a, Layer11B b)
        {
        }
    }

    internal sealed class Layer11A
    {
        public Layer11A(Layer12A a, Layer12B b)
        {
        }
    }

    internal sealed class Layer11B
    {
        public Layer11B(Layer12A a, Layer12B b)
        {
        }
    }

    internal sealed class Layer12A
    {
        public Layer12A(Layer13A a, Layer13B b)
        {
        }
    }

    internal sealed class Layer12B
    {
        public Layer12B(Layer13A a, Layer13B b)
        {
        }
    }

    internal sealed class Layer13A
    {
        public Layer13A(Layer14A a, Layer14B b)
        {
        }
    }

    internal sealed class Layer13B
    {
        public Layer13B(Layer14A a, Layer14B b)
        {
        }
    }

    internal sealed class Layer14A
    {
        public Layer14A(Layer15A a, Layer15B b)
        {
        }
    }

    internal sealed class Layer14B
    {
        public Layer14B(Layer15A a, Layer15B b)
        {
        }
    }

    internal sealed class Layer15A
    {
        public Layer15A(Layer16A a, Layer16B b)
        {
        }
    }

    internal sealed class Layer15B
    {
        public Layer15B(Layer16A a, Layer16B b)
        {
        }
    }

    internal sealed class Layer16A
    {
        public Layer16A(Layer17A a, Layer17B b)
        {
        }
    }

    internal sealed class Layer16B
    {
        public Layer16B(Layer17A a, Layer17B b)
        {
        }
    }

    internal sealed class Layer17A
    {
        public Layer17A(Layer18A a, Layer18B b)
        {
        }
    }

    internal sealed class Layer17B
    {
        public Layer17B(Layer18A a, Layer18B b)
        {
        }
    }

    internal sealed class Layer18A
    {
        public Layer18A(Layer19A a, Layer19B b)
        {
        }
    }

    internal sealed class Layer18B
    {
        public Layer18B(Layer19A a, Layer19B b)
        {
        }
    }

    internal sealed class Layer19A
    {
        public Layer19A(Layer20A a, Layer20B b)
        {
        }
    }

    internal sealed class Layer19B
    {
        public Layer19B(Layer20A a, Layer20B b)
        {
        }
    }

    internal sealed class Layer20A
    {
        public Layer20A(Layer21A a, Layer21B b)
        {
        }
    }

    internal sealed class Layer20B
    {
        public Layer20B(Layer21A a, Layer21B b)
        {
        }
    }

    internal sealed class Layer21A
    {
        public Layer21A(Layer22A a, Layer22B b)
        {
        }
    }

    internal sealed class Layer21B
    {
        public Layer21B(Layer22A a, Layer22B b)
        {
        }
    }

    internal sealed class Layer22A
    {
        public Layer22A(Layer23A a, Layer23B b)
        {
        }
    }

    internal sealed class Layer22B
    {
        public Layer22B(Layer23A a, Layer23B b)
        {
        }
    }

    internal sealed class Layer23A
    {
        public Layer23A(Layer24A a, Layer24B b)
        {
        }
    }

    internal sealed class Layer23B
    {
        public Layer23B(Layer24A a, Layer24B b)
        {
        }
    }

    internal sealed class Layer24A
    {
        public Layer24A(Layer25A a, Layer25B b)
        {
        }
    }

    internal sealed class Layer24B
    {
        public Layer24B(Layer25A a, Layer25B b)
        {
        }
    }

    internal sealed class Layer25A
    {
        public Layer25A(Layer26A a, Layer26B b)
        {
        }
    }

    internal sealed class Layer25B
    {
        public Layer25B(Layer26A a, Layer26B b)
        {
        }
    }

    internal sealed class Layer26A
    {
        public Layer26A(Layer27A a, Layer27B b)
        {
        }
    }

    internal sealed class Layer26B
    {
        public Layer26B(Layer27A a, Layer27B b)
        {
        }
    }

    internal sealed class Layer27A
    {
        public Layer27A(Layer28A a, Layer28B b)
        {
        }
    }

    internal sealed class Layer27B
    {
        public Layer27B(Layer28A a, Layer28B b)
        {
        }
    }

    internal sealed class Layer28A
    {
        public Layer28A(Layer29A a, Layer29B b)
        {
        }
    }

    internal sealed class Layer28B
    {
        public Layer28B(Layer29A a, Layer29B b)
        {
        }
    }

    internal sealed class Layer29A
    {
        public Layer29A(Layer30A a, Layer30B b)
        {
        }
    }

    internal sealed class Layer29B
    {
        public Layer29B(Layer30A a, Layer30B b)
        {
        }
    }

    internal sealed class Layer30A
    {
        public Layer30A(Layer31A a, Layer31B b)
        {
        }
    }

    internal sealed class Layer30B
    {
        public Layer30B(Layer31A a, Layer31B b)
        {
        }
    }

    internal sealed class Layer31A
    {
        public Layer31A(Layer32A a, Layer32B b)
        {
        }
    }

    internal sealed class Layer31B
    {
        public Layer31B(Layer32A a, Layer32B b)
        {
        }
    }

    internal sealed class Layer32A
    {
    }

    internal sealed class Layer32B
    {
    }
}
