namespace TBot.Database.Models;

public class TgChat
{
    public long ChatId { get; set; }       // Telegram chat ID
    public bool IsPrivate { get; set; }
    public string ChatName { get; set; }  
    public string ChatKey { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }
}
