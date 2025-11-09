using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class UpdateInfo
    {
        public long UserId { get; set; }
        public string UserName { get; set; }

        public long ChatId { get; set; }

        public string Message { get; set; }
    }
}
