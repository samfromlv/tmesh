using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class DeliveryStatusWithRecipientId
    {
        public RecipientType Type { get; set; }

        public long? RecipientId { get; set; }
        public DeliveryStatus Status { get; set; }

    }
}
