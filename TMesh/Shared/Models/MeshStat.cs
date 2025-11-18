using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared.Models
{
    public class MeshStat
    {
        public int DupsIgnored { get; set; }
        public int NodeInfoRecieved { get; set; }
        public int TextMessagesRecieved { get; set; }
        public int AckRecieved { get; set; }
        public int TextMessagesSent { get; set; }
        public int AckSent { get; set; }
        public int NakSent { get; set; }
        public int TraceRoutes { get; set; }

        public void Add(MeshStat other)
        {
            DupsIgnored += other.DupsIgnored;
            NodeInfoRecieved += other.NodeInfoRecieved;
            TextMessagesRecieved += other.TextMessagesRecieved;
            AckRecieved += other.AckRecieved;
            TextMessagesSent += other.TextMessagesSent;
            AckSent += other.AckSent;
            NakSent += other.NakSent;
            TraceRoutes += other.TraceRoutes;
        }

        public DateTime IntervalStart { get; set; }
    }
}
