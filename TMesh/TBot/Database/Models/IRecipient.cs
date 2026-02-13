namespace TBot.Database.Models
{
    public interface IRecipient
    {
        public long? RecipientDeviceId { get; }
        public long? RecipientChannelId { get; }
        public byte[] RecipientKey { get; }
        public byte? RecipientChannelXor { get; }

    }
}