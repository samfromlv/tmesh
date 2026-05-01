using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TBot.Analytics.Models;

namespace TBot.Models.MeshMessages
{
    public class NodeInfoMessage: MeshMessage
    {
        public override MeshMessageType MessageType => MeshMessageType.NodeInfo;
        [JsonIgnore]
        public byte[] PublicKey { get; set; }
        public string NodeName { get; set; }

        public Packet Packet { get; set; }

        public NodeInfo NodeInfo { get; set; }
    }
}
