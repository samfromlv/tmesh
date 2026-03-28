namespace TBot.Database.Models;

public class TgChat
{
    public long Id { get; set; }           // Primary key, auto-generated
    public long ChatId { get; set; }       // Telegram chat ID
    public long TelegramUserId { get; set; }
    public string TelegramUserHandle { get; set; } // @username, may be null
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }
    public long? LastChatDeviceId { get; set; }
    public DateTime? LastChatStartDate { get; set; }
}
