namespace TBot.Database.Models;

public class ScheduledMessageVariant
{
    public int Id { get; set; }
    public int ScheduledMessageId { get; set; }
    public string Text { get; set; }
}
