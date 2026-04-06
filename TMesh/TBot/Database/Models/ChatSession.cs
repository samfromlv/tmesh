using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Database.Models
{
    public class ChatSession
    {
        public long ChatId { get; set; }

        public long? DeviceId { get; set; }

        public int? ChannelId { get; set; }

        public DateTime ExpirationDate { get; set; }
    }
}
