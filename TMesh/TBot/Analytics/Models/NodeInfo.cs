using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class NodeInfo
    {
        public long RecordId { get; set; }
        public int? HardwareModel { get; set; }

        public bool IsLicensed { get; set; }
        public bool IsUnmessagable { get; set; }

        public byte Role { get; set; }

        public string UserId { get; set; }
        public string LongName { get; set; }

        public long? MacAddr { get; set; }

        public string ShortName { get; set; }

        public byte[] PublicKey { get; set; }
    }
}
