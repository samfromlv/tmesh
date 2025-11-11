namespace TBot;

public class TBotOptions
{
    public string MqttAddress { get; set; }
    public int MqttPort { get; set; }
    public string MqttUser { get; set; } 
    public string MqttPassword { get; set; } 
    public string MqttMeshtasticTopic { get; set; }
    public string MqttTelegramTopic { get; set; }
    public string TelegramApiToken { get; set; } 
    public string TelegramWebhookSecret { get; set; } 
    public string TelegramUpdateWebhookUrl { get; set; }

    public int TelegramBotMaxConnections { get; set; }
    public string SQLiteConnectionString { get; set; }
    public int OutgoingMessageHopLimit { get; set; }
    public int MeshtasticNodeId { get; set; }
    public string MeshtasticNodeNameShort { get; set; }
    public string MeshtasticNodeNameLong { get; set; }

    public string MeshtasticPublicKeyBase64 { get; set; }
    public string MeshtasticPrivateKeyBase64 { get; set; }


}
