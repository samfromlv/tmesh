using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class NodeInfoMessage: MeshMessage
    {
        public override MeshMessageType MessageType => MeshMessageType.NodeInfo;
        public byte[] PublicKey { get; set; }
        public string NodeName { get; set; }

        

    }
}
