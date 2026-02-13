using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class ChannelKey : IRecipient
    {
        public long Id { get; set; }
        public byte ChannelXor { get; set; }
        public byte[] PreSharedKey { get; set; }

        long? IRecipient.RecipientDeviceId => null;

        byte[] IRecipient.RecipientKey => PreSharedKey;

        byte? IRecipient.RecipientChannelXor => ChannelXor;

        long? IRecipient.RecipientChannelId => Id;
    }
}
