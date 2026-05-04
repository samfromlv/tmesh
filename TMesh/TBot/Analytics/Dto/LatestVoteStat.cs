using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Dto
{
    public class LatestVoteStat
    {
        public int VoteId { get; set; }

        public Instant? LastUpdate { get; set; }

        public byte OptionId { get; set; }

        public int ActiveCount { get; set; }

    }
}
