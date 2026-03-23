using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DeviceName
    {
        public long DeviceId { get; set; }

        public int NetworkId { get; set; }
        public string NodeName { get; set; }

        public DateTime LastNodeInfo { get; set; }
        public DateTime? LastPositionUpdate { get; set; }
    }
}
