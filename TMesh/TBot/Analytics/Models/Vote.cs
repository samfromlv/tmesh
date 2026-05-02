using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class Vote
    {
        public int Id { get; set; }

        public int NetworkId { get; set; }

        public bool Enabled { get; set; }
        public bool IsActive { get; set; }

        public int NodeActiveHoursLimit { get; set; }
        public int UpdateIntervalMinutes { get; set; }

        public Instant StartsAt { get; set; }

        public Instant EndsAt { get; set; }

        public string Name { get; set; }
        public string Description { get; set; }
        public Instant? LastUpdate { get; set; }

        public int? LastSnapshotId { get; set; }

        public ICollection<VoteOption> Options { get; set; }
    }
}
