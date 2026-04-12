using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class DeviceKey: IRecipient
    {
        public long DeviceId { get; set; }
        public int NetworkId { get; set; }


        [JsonIgnore]
        public byte[] PublicKey { get; set; }

        long? IRecipient.RecipientDeviceId => DeviceId;

        byte[] IRecipient.RecipientKey => PublicKey;

        byte? IRecipient.RecipientChannelXor => null;

        int? IRecipient.RecipientPrivateChannelId => null;

        bool? IRecipient.IsSingleDeviceChannel => null;

        int? IRecipient.RecipientPublicChannelId => null;

        int IRecipient.NetworkId => NetworkId;
    }
}
