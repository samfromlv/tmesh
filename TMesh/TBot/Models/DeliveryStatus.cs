using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public enum DeliveryStatus
    {
        Created = 0,
        Queued = 1,
        SentToMqtt = 2,
        Acknowledged = 3,
        Delivered = 4,
        Failed = 5
    }
}
