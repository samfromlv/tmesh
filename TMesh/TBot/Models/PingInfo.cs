using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class PingInfo
    {
        public long DeviceId { get; set; }
        public long PongMessageId { get; set; }
        public int Packets { get; set; }
        public int PingDistanceMeters { get; set; }

        public DateTime LastUpdated { get; set; }


    }
}
