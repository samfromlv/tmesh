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
        AddingChannel_NeedNetwork,
        AddingChannel_NeedName,
        AddingChannel_NeedKey,
        AddingChannel_NeedSingleDevice,
        AddingChannel_NeedCode,
        RemovingDevice,
        RemovingDeviceFromAll,
        RemovingChannel,
        RemovingChannelFromAll,
        PromotingToGateway,
        PromotingToGateway_NeedFirmwareConfirm,
        DemotingFromGateway,
        Admin,
        RegisteringChat_NeedName
    }
}
