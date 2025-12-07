using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class ChannelInternalInfo
    {
        public uint Hash { get; set; }
        public string Name { get; set; }
        public byte[] Psk { get; set; }
    }
}
