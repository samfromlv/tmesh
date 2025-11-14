using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class EncryptedDirectMessage: MeshMessage
    {
        public override MeshMessageType MessageType => MeshMessageType.EncryptedDirectMessage;
    }
}
