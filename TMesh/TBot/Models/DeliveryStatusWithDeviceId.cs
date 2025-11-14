using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DeliveryStatusWithDeviceId
    {
        public long DeviceId { get; set; }
        public DeliveryStatus Status { get; set; }
    }
}
