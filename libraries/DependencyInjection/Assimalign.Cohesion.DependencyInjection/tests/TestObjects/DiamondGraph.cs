using System;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// A 33-layer dependency graph in which both services of every layer depend on both services
/// of the next. It has 66 services but 2^32 paths from the top layer to the bottom, so a
/// validator that walks every path instead of every service never finishes.
/// </summary>
internal static class DiamondGraph
{
    internal static Type[] Services { get; } =
    [
        typeof(Layer0A), typeof(Layer0B),
        typeof(Layer1A), typeof(Layer1B),
        typeof(Layer2A), typeof(Layer2B),
        typeof(Layer3A), typeof(Layer3B),
        typeof(Layer4A), typeof(Layer4B),
        typeof(Layer5A), typeof(Layer5B),
        typeof(Layer6A), typeof(Layer6B),
        typeof(Layer7A), typeof(Layer7B),
        typeof(Layer8A), typeof(Layer8B),
        typeof(Layer9A), typeof(Layer9B),
        typeof(Layer10A), typeof(Layer10B),
        typeof(Layer11A), typeof(Layer11B),
        typeof(Layer12A), typeof(Layer12B),
        typeof(Layer13A), typeof(Layer13B),
        typeof(Layer14A), typeof(Layer14B),
        typeof(Layer15A), typeof(Layer15B),
        typeof(Layer16A), typeof(Layer16B),
        typeof(Layer17A), typeof(Layer17B),
        typeof(Layer18A), typeof(Layer18B),
        typeof(Layer19A), typeof(Layer19B),
        typeof(Layer20A), typeof(Layer20B),
        typeof(Layer21A), typeof(Layer21B),
        typeof(Layer22A), typeof(Layer22B),
        typeof(Layer23A), typeof(Layer23B),
        typeof(Layer24A), typeof(Layer24B),
        typeof(Layer25A), typeof(Layer25B),
        typeof(Layer26A), typeof(Layer26B),
        typeof(Layer27A), typeof(Layer27B),
        typeof(Layer28A), typeof(Layer28B),
        typeof(Layer29A), typeof(Layer29B),
        typeof(Layer30A), typeof(Layer30B),
        typeof(Layer31A), typeof(Layer31B),
        typeof(Layer32A), typeof(Layer32B),
    ];

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
