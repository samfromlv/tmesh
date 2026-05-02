using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class VoteSnapshot
    {
        public int Id { get; set; }
        public int VoteId { get; set; }
        public Instant Timestamp { get; set; }
        public int? PreviousSnapshotId { get; set; }

    }
}
