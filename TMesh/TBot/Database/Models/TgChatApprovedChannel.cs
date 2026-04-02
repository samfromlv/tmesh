namespace TBot.Database.Models;

/// <summary>
/// Tracks Meshtastic channels that a Telegram chat user has previously approved for chat.
/// Subsequent chat requests from these channels are auto-approved.
/// </summary>
public class TgChatApprovedChannel
{
    public long Id { get; set; }        // Primary key, auto-generated
    public long TgChatId { get; set; }  // FK to TgChat.Id
    public int ChannelId { get; set; }  // FK to Channel.Id
    public DateTime CreatedUtc { get; set; }
}
