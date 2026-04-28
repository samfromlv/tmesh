using System;
using System.Collections.Generic;

namespace Shared.Models
{
    public class NetworkStats
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public MeshStat Mesh5Min { get; set; }
        public MeshStat Mesh15Min { get; set; }
        public MeshStat Mesh1Hour { get; set; }

        public int DeviceChatRegistrations { get; set; }
        public int ChannelChatRegistrations { get; set; }

        public int Devices24h { get; set; }
        public int MfVoteDevices24h { get; set; }
        public int LfVoteDevices24h { get; set; }
        public int NoVoteDevices24h { get; set; }
        public int Devices { get; set; }

        public int TelemetrySaved24H { get; set; }

        public Dictionary<string, DateTime?> GatewaysLastSeen { get; set; }
    }
}
