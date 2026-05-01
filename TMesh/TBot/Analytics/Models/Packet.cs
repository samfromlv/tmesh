using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class Packet
    {
        public long RecordId { get; set; }

        public NodaTime.Instant Timestamp { get; set; }

        public uint PacketId { get; set; }

        public uint From { get; set; }

        public uint To { get; set; }

        public byte Channel { get; set; }

        public byte NextHop { get; set; }

        public byte HopLimit { get; set; }

        public byte HopStart { get; set; }

        public bool WantAck { get; set; }

        public bool ViaMqtt { get; set; }

        public byte RelayNode { get; set; }
        public string MqttChannel { get; set; }
        public uint GatewayId { get; set; }

        public bool IsTMeshGateway { get; set; }

        public uint RxTimestamp { get; set; }

        public float RxSnr { get; set; }

        public int RxRssi { get; set; }

        public uint TxAfter { get; set; }

        public bool PkiEncrypted { get; set; }

        public int? DecodedByPublicChannelId { get; set; }

        public byte Transport { get; set; }

        public byte Priority { get; set; }

        public bool OkToMqttFlag { get; set; }

        public bool NeedReplyFlag { get; set; }
        public bool WantResponse { get; set; }

        public uint Dest { get; set; }

        public bool IsEmoji { get; set; }

        public int PortNum { get; set; }

        public uint RequestId { get; set; }

        public uint ReplyId { get; set; }
        public uint Source { get; set; }
        public bool Flagged { get; set; }
    }
}
