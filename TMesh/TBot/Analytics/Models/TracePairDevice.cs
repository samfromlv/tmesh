using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Analytics.Models
{
    public class TracePairDevice
    {
        public int NetworkId { get; set; }
        public LocalDate RecDate { get; set; }
        public long Id { get; set; }
        public string Name { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public DeviceRole? Role { get; set; }
        public string PresetName { get; set; }
    }
}
