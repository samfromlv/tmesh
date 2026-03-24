using Meshtastic.Protobufs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.Queue
{
    public class QueuedMessage
    {
        public int NetworkId { get; set; }

        public ServiceEnvelope Message { get; set; }

        public long? RelayThroughGatewayId { get; set; }

    }
}
