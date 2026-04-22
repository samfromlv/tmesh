using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Shared.Models
{
    public class MeshStat
    {
        [JsonIgnore]
        public int NetworkId { get; set; }
        public int DupsIgnored { get; set; }
        public int LinkTraces { get; set; }
        public int NodeInfoRecieved { get; set; }
        public int DirectTextMessagesRecieved { get; set; }
        public int PublicTextMessagesRecieved { get; set; }
        public int PrivateChannelTextMessagesRecieved { get; set; }
        public int AckRecieved { get; set; }
        public int NakRecieved { get; set; }
        public int TextMessagesSent { get; set; }
        public int WelcomeMessagesSent { get; set; }
        public int AckSent { get; set; }
        public int NakSent { get; set; }
        public int PongSent { get; set; }
        public int TraceRoutes { get; set; }
        public int BridgeDirectMessagesToGateways { get; set; }

        public void Add(MeshStat other)
        {
            DupsIgnored += other.DupsIgnored;
            LinkTraces += other.LinkTraces;
            NodeInfoRecieved += other.NodeInfoRecieved;
            DirectTextMessagesRecieved += other.DirectTextMessagesRecieved;
            PublicTextMessagesRecieved += other.PublicTextMessagesRecieved;
            PrivateChannelTextMessagesRecieved += other.PrivateChannelTextMessagesRecieved;
            AckRecieved += other.AckRecieved;
            NakRecieved += other.NakRecieved;
            TextMessagesSent += other.TextMessagesSent;
            WelcomeMessagesSent += other.WelcomeMessagesSent;
            AckSent += other.AckSent;
            NakSent += other.NakSent;
            TraceRoutes += other.TraceRoutes;
            PongSent += other.PongSent;
            BridgeDirectMessagesToGateways += other.BridgeDirectMessagesToGateways;
        }

        public DateTime IntervalStart { get; set; }
    }
}
