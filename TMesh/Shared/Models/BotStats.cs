using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared.Models
{
    public class BotStats
    {
        public MeshStat Mesh5Min { get; set; }
        public MeshStat Mesh15Min { get; set; }
        public MeshStat Mesh1Hour { get; set; }

        public int ChatRegistrations { get; set; }

        public int Devices { get; set; }

        public DateTime LastUpdate { get; set; }

        public DateTime Started { get; set; }

        public Dictionary<string, DateTime?> GatewaysLastSeen { get; set; }
    }
}
