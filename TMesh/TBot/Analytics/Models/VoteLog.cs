using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class VoteLog
    {
        public enum VoteChangeReason: byte
        {
            FirstVote = 0,
            VoteChanged = 1,
            VoteExpired = 2
        }

        public int Id { get; set; }

        public int VoteId { get; set; }

        public VoteChangeReason Reason { get; set; }

        public uint DeviceId { get; set; }

        public Instant LogCreated { get; set; }

        public Instant ChangeMade  { get; set; }

        public string NewLongName { get; set; }

        public byte OldOptionId { get; set; }

        public byte NewOptionId { get; set; }

        public uint? MeshPacketId { get; set; }

        public int SnapshotId { get; set; }

    }
}
