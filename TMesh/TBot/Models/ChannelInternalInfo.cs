using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models
{
    public class ChannelInternalInfo: IRecipient
    {
        public byte Hash { get; set; }
        public string Name { get; set; }
        public byte[] Psk { get; set; }

        long? IRecipient.RecipientDeviceId => null;

        long? IRecipient.RecipientChannelId => null;

        byte[] IRecipient.RecipientKey => Psk;

        byte? IRecipient.RecipientChannelXor => Hash;
    }
}
