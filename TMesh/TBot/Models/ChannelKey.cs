using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class ChannelKey : IRecipient
    {
        public long Id { get; set; }

        public int NetworkId { get; set; }
        public byte ChannelXor { get; set; }

        [JsonIgnore]
        public byte[] PreSharedKey { get; set; }
        public bool IsSingleDevice { get; set; }

        long? IRecipient.RecipientDeviceId => null;

        byte[] IRecipient.RecipientKey => PreSharedKey;

        byte? IRecipient.RecipientChannelXor => ChannelXor;

        int? IRecipient.RecipientPrivateChannelId => (int)Id;

        bool? IRecipient.IsSingleDeviceChannel => IsSingleDevice;
    }
}
