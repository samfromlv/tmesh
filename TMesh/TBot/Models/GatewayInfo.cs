using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class GatewayInfo
    {
        public long DeviceId { get; set; }

        public DateTime? LastSeen { get; set; }

        public int NetworkId { get; set; }

        public UplinkPrivacyMode PrivacyMode { get; set; }
    }
}
