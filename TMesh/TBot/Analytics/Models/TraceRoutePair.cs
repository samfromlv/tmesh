using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class TraceRoutePair
    {
        public long Id { get; set; }
        public int NetworkId { get; set; }
        public LocalDate RecDate { get; set; }
        public uint ToDeviceId { get; set; }
        public uint FromDeviceId { get; set; }
        public byte Hops { get; set; }
        public float? DirectSnr { get; set; }
        public int? LinkLengthMeters { get; set; }
        public int? DistanceBetweenDevices { get; set; }
    }
}
