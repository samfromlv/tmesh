using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class TextMessage
    {
        public string Text { get; set; }
        public long DeviceId { get; set; }

        public byte[] PublicKey { get; set; }
    }
}
