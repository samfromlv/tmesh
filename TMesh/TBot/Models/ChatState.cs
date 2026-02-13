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
        AddingDevice_NeedId,
        AddingDevice_NeedCode,
        AddingChannel_NeedName,
        AddingChannel_NeedKey,
        AddingChannel_NeedCode,
        RemovingDevice,
        RemovingDeviceFromAll,
        Admin
    }
}
