using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class MeshtasticMessageStatus
    {
        public TgMessageUid[] TgMessageUids { get; set; }

        public int? BotReplyId { get; set; }

        public string ReplyText { get; set; }

        public DateTime? EstimatedSendDate { get; set; }

        public int SeenByGateways { get; set; }
        public bool IsPublicChannelOnly { get; set; }
        public Dictionary<long, DeliveryStatusWithRecipientId> MeshMessages { get; set; }

       

    }
}
