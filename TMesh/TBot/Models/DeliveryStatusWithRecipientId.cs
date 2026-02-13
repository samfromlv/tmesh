using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DeliveryStatusWithRecipientId
    {
        public bool IsDevice { get; set; }
        public long? RecipientId { get; set; }
        public DeliveryStatus Status { get; set; }
    }
}
