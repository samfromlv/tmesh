using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public abstract class MeshMessage
    {
        public abstract MeshMessageType MessageType { get; }
        public long DeviceId { get; set; }
    }
}
