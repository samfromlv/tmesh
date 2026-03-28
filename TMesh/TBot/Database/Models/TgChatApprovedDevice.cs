namespace TBot.Database.Models;

/// <summary>
/// Tracks Meshtastic devices that a Telegram chat user has previously approved for chat.
/// Subsequent chat requests from these devices are auto-approved.
/// </summary>
public class TgChatApprovedDevice
{
    public long Id { get; set; }        // Primary key, auto-generated
    public long TgChatId { get; set; }  // FK to TgChat.Id
    public long DeviceId { get; set; }
    public DateTime CreatedUtc { get; set; }
}
