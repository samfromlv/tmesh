namespace TBot.Database.Models;

public class ScheduledMessage
{
    public int Id { get; set; }
    public int PublicChannelId { get; set; }
    public string Text { get; set; }
    public int IntervalMinutes { get; set; }
    public DateTime? LastSentUtc { get; set; }
    public bool Enabled { get; set; }
}
