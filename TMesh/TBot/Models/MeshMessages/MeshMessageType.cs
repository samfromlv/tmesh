using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.MeshMessages
{
    public enum MeshMessageType
    {
        NodeInfo,         
        Text,
        EncryptedDirectMessage,
        AckMessage,
        TraceRoute,
        Position
    }
}
