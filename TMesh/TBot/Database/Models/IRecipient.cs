using System.Text.Json.Serialization;

namespace TBot.Database.Models
{
    public interface IRecipient
    {
        public long? RecipientDeviceId { get; }
        public int? RecipientPrivateChannelId { get; }
        [JsonIgnore]
        public byte[] RecipientKey { get; }
        public byte? RecipientChannelXor { get; }

        public int NetworkId { get; }

        public bool? IsSingleDeviceChannel { get; }

        public RecipientType RecipientType => RecipientDeviceId.HasValue ? RecipientType.Device : RecipientType.Channel;

        public long? RecipientId => RecipientDeviceId ?? RecipientPrivateChannelId;

        public bool IsPublicChannel => RecipientChannelXor.HasValue
            && RecipientDeviceId == null
            && RecipientPrivateChannelId == null;
    }
}