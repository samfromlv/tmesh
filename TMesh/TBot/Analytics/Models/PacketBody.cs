using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class PacketBody
    {
        public long RecordId { get; set; }

        public byte[] Body { get; set; }

    }
}
