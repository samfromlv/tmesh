using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DeviceWithNameAndKey
    {
        public long DeviceId { get; set; }
        public string NodeName { get; set; }
        public byte[] PublicKey { get; set; }

        public string RegisteredByUser { get; set; }
    }
}
