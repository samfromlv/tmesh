using Meshtastic.Protobufs;

namespace TBot.Models.Queue
{
    public class QueuedMessage
    {
        public int NetworkId { get; set; }

        public ServiceEnvelope Message { get; set; }

        public bool NoUplink { get; set; }

        public long? RelayThroughGatewayId { get; set; }

    }
}
