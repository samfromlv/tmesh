using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TBot.Models.MeshMessages
{
    public class TextMessage: MeshMessage
    {
        override public MeshMessageType MessageType => MeshMessageType.Text;
        [JsonIgnore]
        public string Text { get; set; }

        [JsonIgnore]
        public bool IsEmoji { get; set; }

        public bool IsDirectMessage { get; set; }
        public long ReplyTo { get; set; }
    }
}
