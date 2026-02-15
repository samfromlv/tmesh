using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Database.Models
{
    public class GatewayRegistration
    {
        public long DeviceId { get; set; }
        public System.DateTime CreatedUtc { get; set; }
        public System.DateTime UpdatedUtc { get; set; }
    }
}
