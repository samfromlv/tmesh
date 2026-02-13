namespace TBot.Database.Models;

public class Channel: IRecipient
{
    // Primary key
    public int Id { get; set; }

    public string Name { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] Key { get; set; }
    public byte XorHash { get; set; }
    public System.DateTime CreatedUtc { get; set; }

    long? IRecipient.RecipientDeviceId => null;

    byte[] IRecipient.RecipientKey => Key;

    byte? IRecipient.RecipientChannelXor => XorHash;

    long? IRecipient.RecipientChannelId => Id;
}
