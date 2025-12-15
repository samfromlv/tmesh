using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.MeshMessages
{
    public class PositionMessage : MeshMessage
    {
        override public MeshMessageType MessageType => MeshMessageType.Position;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public float? Altitude { get; set; }
        public int? HeadingDegrees { get; set; }

        public double AccuracyMeters { get; set; }

        public bool SentToOurNodeId { get; set; }

    }
}
