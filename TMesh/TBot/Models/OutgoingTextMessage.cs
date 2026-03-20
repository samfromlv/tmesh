using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class OutgoingTextMessage
    {
        public IRecipient Recipient { get; set; }

        public long TelegramChatId { get; set; }

        public int TelegramMessageId { get; set; }
        public string Text { get; set; }
    }
}
