using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Database.Models
{
    public class DeviceRegistration
    {
        public long Id { get; set; } // Primary key, auto-generated
        public long TelegramUserId { get; set; }
        public long ChatId { get; set; }
        public long DeviceId { get; set; }

        public string UserName { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
