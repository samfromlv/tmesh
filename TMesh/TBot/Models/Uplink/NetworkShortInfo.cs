using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.Uplink
{
    public class NetworkShortInfo
    {
        public int Id { get; set; }
        public string ShortName { get; set; }
        public bool SaveAnalytics { get; set; }
        public string[] PublicChannelNames { get; set; }
    }
}
