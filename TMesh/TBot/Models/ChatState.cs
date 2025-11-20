using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public enum ChatState
    {
        Default,
        Adding_NeedDeviceId,
        Adding_NeedCode,
        RemovingDevice,
        Admin
    }
}
