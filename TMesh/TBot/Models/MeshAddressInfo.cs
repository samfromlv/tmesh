using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class MeshAddressInfo
    {
        public long From { get; set; }
        public long To { get; set; }

        public bool IsPkiEncrypted { get; set; }

        public byte XorHash { get; set; }
    }
}
