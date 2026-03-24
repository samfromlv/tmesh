namespace TBot.Database.Models;

public class PublicChannel: IRecipient
{
    // Primary key
    public int Id { get; set; }
    public int NetworkId { get; set; }
    public string Name { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] Key { get; set; }
    public byte XorHash { get; set; }

    public bool IsPrimary { get; set; }
    public System.DateTime CreatedUtc { get; set; }

    long? IRecipient.RecipientDeviceId => null;

    byte[] IRecipient.RecipientKey => Key;

    byte? IRecipient.RecipientChannelXor => XorHash;

    long? IRecipient.RecipientPrivateChannelId => null;

    bool? IRecipient.IsSingleDeviceChannel => null;
}
