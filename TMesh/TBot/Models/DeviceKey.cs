using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class DeviceKey: IDeviceKey
    {
        public long DeviceId { get; set; }
        public byte[] PublicKey { get; set; }
    }
}
