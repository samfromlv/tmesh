using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.MeshMessages
{
    public class DeviceMetricsMessage : MeshMessage
    {
        override public MeshMessageType MessageType => MeshMessageType.DeviceMetrics;

        public float? ChannelUtilization { get; set; }

        public float? AirUtilization { get; set; }
    }
}
