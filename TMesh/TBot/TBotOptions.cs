namespace TBot;

public class TBotOptions
{
    public string MqttAddress { get; set; }
    public int MqttPort { get; set; }
    public string MqttUser { get; set; } 
    public string MqttPassword { get; set; } 
    public string MqttMeshtasticTopicPrefix { get; set; }
    public string MqttTelegramTopic { get; set; }
    public string TelegramApiToken { get; set; } 
    public string TelegramWebhookSecret { get; set; } 
    public string TelegramUpdateWebhookUrl { get; set; }
    public string TelegramBotUserName { get; set; }

    public int TelegramBotMaxConnections { get; set; }
    public string SQLiteConnectionString { get; set; }
    public int OutgoingMessageHopLimit { get; set; }
    public int OwnNodeInfoMessageHopLimit { get; set; }
    public int MeshtasticNodeId { get; set; }
    public string MeshtasticNodeNameShort { get; set; }
    public string MeshtasticNodeNameLong { get; set; }


    public string MeshtasticPrimaryChannelName { get; set; }
    public string MeshtasticPrimaryChannelPskBase64 { get; set; }
    public string MeshtasticPublicKeyBase64 { get; set; }
    public string MeshtasticPrivateKeyBase64 { get; set; }

    public int MeshtasticMaxOutgoingMessagesPerMinute { get; set; }

    public int SentTBotNodeInfoEverySeconds { get; set; }


}
