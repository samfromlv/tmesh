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
        Starting_NeedPrivacyConfim,
        KillingChat_NeedConfirm,
        AddingDevice_NeedId,
        AddingDevice_NeedPrivacyConfim,
        AddingDevice_NeedCode,
        AddingChannel_NeedPrivacyConfim,
        AddingChannel_NeedNetwork,
        AddingChannel_NeedName,
        AddingChannel_NeedKey,
        AddingChannel_NeedInsecureKeyConfirm,
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
