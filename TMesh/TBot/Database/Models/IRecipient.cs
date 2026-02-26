namespace TBot.Database.Models
{
    public interface IRecipient
    {
        public long? RecipientDeviceId { get; }
        public long? RecipientChannelId { get; }
        public byte[] RecipientKey { get; }
        public byte? RecipientChannelXor { get; }

        public bool? IsSingleDeviceChannel { get; }

        public RecipientType RecipientType => RecipientDeviceId.HasValue ? RecipientType.Device : RecipientType.Channel;

        public long? RecipientId => RecipientDeviceId ?? RecipientChannelId;
    }
}