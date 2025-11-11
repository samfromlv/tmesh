namespace TBot.Database.Models;

public class Device
{
    // Primary key
    public long DeviceId { get; set; }
    // 32 bytes key stored as blob
    public byte[] PublicKey { get; set; }
    public string NodeName { get; set; }
    public System.DateTime UpdatedUtc { get; set; }
}
