using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class VoteOption
    {
        public int VoteId { get; set; }

        public byte OptionId { get; set; }

        public string Name { get; set; }

        public string Prefix { get; set; }

        public Vote Vote { get; set; }

    }
}
