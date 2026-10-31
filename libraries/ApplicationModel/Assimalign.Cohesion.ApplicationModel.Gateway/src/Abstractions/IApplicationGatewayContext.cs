using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public interface IApplicationGatewayContext
{
    IApplicationModel Model { get; }

    IApplicationInstancePublisher Publisher { get; }

    IApplicationGatewayStore Store { get; }
}
