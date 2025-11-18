using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Models.MeshMessages;

namespace TBot.Models
{
    public class EncryptedDirectMessage: MeshMessage
    {
        public override MeshMessageType MessageType => MeshMessageType.EncryptedDirectMessage;
    }
}
