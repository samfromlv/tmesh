using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DeviceAndGatewayId
    {
        public long DeviceId { get; set; }
        public long GatewayId { get; set; }

        public int ReplyHopLimit { get; set; }
    }
}
