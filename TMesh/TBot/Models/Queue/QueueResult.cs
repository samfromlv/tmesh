using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.Queue
{
    public class QueueResult
    {
        public long MessageId { get; set; }

        public TimeSpan EstimatedSendDelay { get; set; }
    }
}
