using Meshtastic.Protobufs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class NetworkServiceEnvelope
    {
        public ServiceEnvelope Envelope { get; set; }

        public int? NetworkId { get; set; }
    }
}
