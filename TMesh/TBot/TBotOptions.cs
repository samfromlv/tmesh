namespace TBot;

public class TBotOptions
{
    public string MqttAddress { get; set; }
    public int MqttPort { get; set; }
    public string MqttUser { get; set; }
    public string MqttPassword { get; set; }
    public string MqttTProxyTopicPrefix { get; set; }
    public string TelegramApiToken { get; set; }
    public string TelegramWebhookSecret { get; set; }
    public string TelegramUpdateWebhookUrl { get; set; }

    public int TelegramBotMaxConnections { get; set; }
}
