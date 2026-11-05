using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Database.Models
{
    public class GatewayRegistration
    {
        public long DeviceId { get; set; }
        public int NetworkId { get; set; }

        // 32 bytes key stored as blob
        [System.Text.Json.Serialization.JsonIgnore]
        public byte[] VirtualNodePublicKey { get; set; }

        // 32 bytes key stored as blob
        [System.Text.Json.Serialization.JsonIgnore]
        public byte[] VirtualNodePrivateKey { get; set; }

        public long VirtualNodeId { get; set; }

        public UplinkPrivacyMode PrivacyMode { get; set; }

        public System.DateTime CreatedUtc { get; set; }
        public System.DateTime UpdatedUtc { get; set; }
        public System.DateTime? LastSeenUtc { get; set; }
    }
}
