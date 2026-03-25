using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared.Models
{
    public class BotStats
    {
        public List<NetworkStats> Networks { get; set; }

        public DateTime LastUpdate { get; set; }

        public DateTime Started { get; set; }
    }
}
