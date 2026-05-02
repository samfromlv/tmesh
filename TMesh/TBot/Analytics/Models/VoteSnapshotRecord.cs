using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class VoteSnapshotRecord
    {
        public int SnapshotId { get; set; }
        public uint DeviceId { get; set; }
        public string LongName { get; set; }
        public byte OptionId { get; set; }
    }
}
