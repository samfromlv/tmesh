using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class AckMessage: MeshMessage
    {
        override public MeshMessageType MessageType => MeshMessageType.AckMessage;

        public bool Success { get; set; }
        public bool IsPkiEncrypted { get; set; }
        public long AckedMessageId { get; set; }

    }
}
