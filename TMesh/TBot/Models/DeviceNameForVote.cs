using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DeviceNameForVote
    {
        public long DeviceId { get; set; }
        public int NetworkId { get; set; }
        public string NodeName { get; set; }

        public DateTime UpdatedUtc { get; set; }
        public DateTime NodeCreatedUtc { get; set; }

        public long? LastNodeInfoPacketId { get; set; }
    }
}
