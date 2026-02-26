using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class DeviceKey: IRecipient
    {
        public long DeviceId { get; set; }
        public byte[] PublicKey { get; set; }

        long? IRecipient.RecipientDeviceId => DeviceId;

        byte[] IRecipient.RecipientKey => PublicKey;

        byte? IRecipient.RecipientChannelXor => null;

        long? IRecipient.RecipientChannelId => null;

        bool? IRecipient.IsSingleDeviceChannel => null;
    }
}
