using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.Uplink
{
    public class PacketUplinkInfo
    {
        public OkToMqttStatus MqttStatus { get; set; }
        public string OverrideChannelName { get; set; }
    }
}
