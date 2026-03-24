using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using k8s;
using k8s.KubeConfigModels;

namespace Assimalign.Cohesion.Synthara;

public class KubernetesArchitecture
{
    public KubernetesArchitecture()
    {
        var config = KubernetesClientConfiguration.BuildConfigFromConfigObject(new()
        {
            Clusters = [
            new Cluster()
            {
                Name = "",
                ClusterEndpoint = new ClusterEndpoint()
                {
                    Server = "",
                },
            },
        ],
        });

        using var client = new Kubernetes(config);

        client.Node.
    }
}
