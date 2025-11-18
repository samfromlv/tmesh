using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.MeshMessages
{
    public abstract class MeshMessage
    {
        public abstract MeshMessageType MessageType { get; }
        public long DeviceId { get; set; }

        public int HopLimit { get; set; }
        public int HopStart { get; set; }
        public int Id { get; set; }
    }
}
