using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class LinkTrace
    {
        public long Id { get; set; }
        public uint PacketId { get; set; }
        public uint FromGatewayId { get; set; }
        public uint ToGatewayId { get; set; }
        public byte? Step { get; set; }
        public Instant Timestamp { get; set; }
        public LocalDate RecDate { get; set; }
        public double ToLatitude { get; set; }
        public double ToLongitude { get; set; }

    }
}
