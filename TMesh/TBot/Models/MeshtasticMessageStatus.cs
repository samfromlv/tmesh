using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class MeshtasticMessageStatus
    {
        public long TelegramChatId { get; set; }

        public int TelegramMessageId { get; set; }

        public int? BotReplyId { get; set; }

        public DateTime? EstimatedSendDate { get; set; }

        public Dictionary<long, DeliveryStatusWithDeviceId> MeshMessages { get; set; }

       

    }
}
