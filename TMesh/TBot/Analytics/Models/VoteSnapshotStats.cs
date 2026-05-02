using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class VoteSnapshotStats
    {
        public int SnapshotId { get; set; }

        public byte OptionId { get; set; }

        public int ActiveCount { get; set; }

        public int DeltaFromLastSnapshot { get; set; }

    }
}
