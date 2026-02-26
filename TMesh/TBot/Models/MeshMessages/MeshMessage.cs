using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.MeshMessages
{
    public abstract class MeshMessage
    {
        public long Id { get; set; }
        public abstract MeshMessageType MessageType { get; }
        public long DeviceId { get; set; }
        public long? ChannelId { get; set; }

        public bool IsSingleDeviceChannel { get; set; }

        public int HopLimit { get; set; }
        public int HopStart { get; set; }

        public bool NeedAck { get; set; }

        public long GatewayId { get; set; }

        public int GetSuggestedReplyHopLimit() => MeshtasticService.GetSuggestedReplyHopLimit(this);
    }
}
