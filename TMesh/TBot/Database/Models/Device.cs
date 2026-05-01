namespace TBot.Database.Models;

public class Device : IRecipient
{
    // Primary key
    public long DeviceId { get; set; }
    public int NetworkId { get; set; }

    // 32 bytes key stored as blob
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] PublicKey { get; set; }
    public string NodeName { get; set; }

    public int? HardwareModel { get; set; }

    public long? MacAddress { get; set; }
    public System.DateTime CreatedUtc { get; set; }
    public System.DateTime UpdatedUtc { get; set; }
    public bool HasRegistrations { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime? LocationUpdatedUtc { get; set; }
    public int? AccuracyMeters { get; set; }

    long? IRecipient.RecipientDeviceId => DeviceId;

    byte[] IRecipient.RecipientKey => PublicKey;

    byte? IRecipient.RecipientChannelXor => null;

    int? IRecipient.RecipientPrivateChannelId => null;

    bool? IRecipient.IsSingleDeviceChannel => null;

    int? IRecipient.RecipientPublicChannelId => null;

    int IRecipient.NetworkId => NetworkId;
}
