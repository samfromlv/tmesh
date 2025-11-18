using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.MeshMessages
{
    public class TextMessage: MeshMessage
    {
        override public MeshMessageType MessageType => MeshMessageType.Text;
        public string Text { get; set; }
    }
}
