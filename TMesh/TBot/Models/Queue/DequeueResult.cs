using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.Queue
{
    public enum DequeueResult: byte
    {
        No = 0,
        Delay = 1,
        Yes = 2
    }
}
