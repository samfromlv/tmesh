namespace TBot.Database.Models;

public class Device: IDeviceKey
{
    // Primary key
    public long DeviceId { get; set; }
    // 32 bytes key stored as blob

    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] PublicKey { get; set; }
    public string NodeName { get; set; }
    public System.DateTime CreatedUtc { get; set; }
    public System.DateTime UpdatedUtc { get; set; }
}
