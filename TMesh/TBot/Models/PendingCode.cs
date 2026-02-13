using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class PendingCode
    {
        public int Tries { get; set; }
        public string Code { get; set; }
        public long? DeviceId { get; set; }
        public string ChannelName { get; set; }
        public byte[] ChannelKey { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
}
