namespace TBot.Database.Models
{
    public interface IDeviceKey
    {
        public long DeviceId { get; }
        public byte[] PublicKey { get; }

    }
}