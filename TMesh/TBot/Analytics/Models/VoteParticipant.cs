using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static TBot.Analytics.Models.VoteLog;

namespace TBot.Analytics.Models
{
    public class VoteParticipant
    {
        public int VoteId { get; set; }

        public uint DeviceId { get; set; }

        public string LongName { get; set; }

        public NodaTime.Instant FirstVote { get; set; }

        public NodaTime.Instant LastVote { get; set; }

        public NodaTime.Instant LastVoteChange { get; set; }
        public NodaTime.Instant NodeRegistered { get; set; }
        public NodaTime.Instant Modified { get; set; }

        public byte CurrentOptionId { get; set; }
        public byte PreviousOptionId { get; set; }

        public uint? VotePacketId { get; set; }

        public bool IsNoVote { get; set; }

        public int VoteCount { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        public int? CityDistrictId { get; set; }

        public VoteChangeReason UpdateReason { get; set; }


    }
}
