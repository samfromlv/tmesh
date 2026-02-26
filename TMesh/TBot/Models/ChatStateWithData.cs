using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class ChatStateWithData
    {
        public ChatState State { get; set; }

        public string ChannelName { get; set; }

        public byte[] ChannelKey { get; set; }

        public bool? IsSingleDevice { get; set; }
    }
}
