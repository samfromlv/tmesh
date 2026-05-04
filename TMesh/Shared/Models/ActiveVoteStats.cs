using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared.Models
{
    public class ActiveVoteStats
    {
        public int VoteId { get; set; }

        public long LastUpdateTimestampSec { get; set; }

        public List<VoteChoice> Stats { get; set; }
    }
}
