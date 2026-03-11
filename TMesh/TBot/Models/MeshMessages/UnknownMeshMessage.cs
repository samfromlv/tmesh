using Meshtastic.Protobufs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models.MeshMessages
{
    public class UnknownMeshMessage: MeshMessage
    {
        public override MeshMessageType MessageType => MeshMessageType.Unknown;
    }
}
