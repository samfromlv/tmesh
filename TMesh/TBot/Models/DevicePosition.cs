using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DevicePosition
    {
        public long DeviceId { get; set; }
        public string NodeName { get; set; }

        public DateTime? LastPositionUpdate { get; set; }

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public int? AccuracyMeters { get; set; }
    }
}
