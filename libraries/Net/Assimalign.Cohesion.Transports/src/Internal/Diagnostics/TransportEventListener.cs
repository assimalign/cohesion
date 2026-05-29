using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Assimalign.Cohesion.Transports.Internal.Sockets
{
    // Reference: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventsource-collect-and-view-traces
    internal class SocketEventListener : EventListener
    {
        [ThreadStatic] 
        private static bool _insideCallback;


        

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
           
            // if our callback triggered the event to occur recursively
            // exit now to avoid infinite recursion
            if (_insideCallback)
            {
                return;
            }
            try
            {
                _insideCallback = true;
                // do callback work
            }
            finally
            {
                _insideCallback = false;
            }
        }
    }
}
