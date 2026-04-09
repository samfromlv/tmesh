using Meshtastic.Protobufs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.MeshMessages
{
    public class TraceRouteMessage : MeshMessage
    {
        override public MeshMessageType MessageType => MeshMessageType.TraceRoute;

        public RouteDiscovery RouteDiscovery { get; set; }

        public long RequestId { get; set; }

        public bool WantsResponse { get; set; }
        public int RxSnrRounded { get; set; }
    }



}
