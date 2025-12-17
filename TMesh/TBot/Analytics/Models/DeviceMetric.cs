using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class DeviceMetric
    {
        public uint DeviceId { get; set; }
        public DateTime Timestamp { get; set; }

        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public DateTime LocationUpdatedUtc { get; set; }
        public int? AccuracyMeters { get; set; }

        public float? ChannelUtil { get; set; }
        public float? AirUtil { get; set; }



    }
}
