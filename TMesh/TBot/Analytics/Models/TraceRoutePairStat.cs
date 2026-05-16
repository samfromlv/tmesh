using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class TraceRoutePairStat
    {
        public int NetworkId { get; set; }
        public LocalDate RecDate { get; set; }
        public uint ToDeviceId { get; set; }
        public uint FromDeviceId { get; set; }
        public short Count { get; set; }
        public short DirectCount { get; set; }
        public float? AvgDirectSnr { get; set; }
        public float AvgHops { get; set; }
        public int? AvgDirectDistance { get; set; }
        public int? AvgLinkLength { get; set; }
        public int WithDistanceCount { get; set; }
        public int WithLinkLengthCount { get; set; }

    }
}
