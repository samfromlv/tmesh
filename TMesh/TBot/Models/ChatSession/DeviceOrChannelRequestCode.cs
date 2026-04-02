using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.ChatSession
{
    public class DeviceOrChannelRequestCode
    {
        public long? DeviceId { get; set; }
        public int? ChannelId { get; set; }

        public string Code { get; set; }
    }
}
