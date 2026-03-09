using TBot.Models;

namespace TBot;

public class TBotOptions
{
    public string MqttAddress { get; set; }
    public int MqttPort { get; set; }
    public string MqttUser { get; set; } 
    public string MqttPassword { get; set; }
    public bool MqttAllowUntrustedCertificates { get; set; }
    public bool MqttUseTls { get; set; }
    public string MqttMeshtasticTopicPrefix { get; set; }
    public string MqttTelegramTopic { get; set; }
    public string MqttStatusTopic { get; set; }
    public string TelegramApiToken { get; set; } 
    public string TelegramWebhookSecret { get; set; } 
    public string TelegramUpdateWebhookUrl { get; set; }
    public string TelegramBotUserName { get; set; }

    public int TelegramBotMaxConnections { get; set; }
    public string SQLiteConnectionString { get; set; }
    public string AnalyticsPostgresConnectionString { get; set; }
    public int OutgoingMessageHopLimit { get; set; }
    public int OwnNodeInfoMessageHopLimit { get; set; }
    public long MeshtasticNodeId { get; set; }
    public string MeshtasticNodeNameShort { get; set; }
    public string MeshtasticNodeNameLong { get; set; }
    public string AdminPassword { get; set; }

    public string MeshtasticPrimaryChannelName { get; set; }
    public string MeshtasticPrimaryChannelPskBase64 { get; set; }
    public ChannelInfo[] MeshtasticSecondayChannels { get; set; }
    public string MeshtasticPublicKeyBase64 { get; set; }
    public string MeshtasticPrivateKeyBase64 { get; set; }

    public int MeshtasticMaxOutgoingMessagesPerMinute { get; set; }

    public int SentTBotNodeInfoEverySeconds { get; set; }

    public long[] GatewayNodeIds { get; set; }

    public int DirectGatewayRoutingSeconds { get; set; }

    public bool BridgeDirectMessagesToGateways { get; set; }
    public bool ReplyToPublicPingsViaDirectMessage { get; set; }

    public string[] PingWords { get; set; }

    public Texts Texts { get; set; }

    public string TimeZone { get; set; }

    public string DefaultMqttPasswordDeriveSecret { get; set; }

    public Dictionary<string, string> MqttUserPasswordDeriveSecrets { get; set; }

    public bool UpgradeDbOnStart { get; set; }

    public string PublicMqttAddress { get; set; }
    public string PublicMqttTopic { get; set; }
    public string PublicFlasherAddress { get; set; }
    public int InactiveGatewayCleanupDays { get; set; }

    /// <summary>
    /// Zero or more public MQTT servers to monitor for TMesh gateway telemetry packets (map gateways).
    /// </summary>
    public MapMqttServerOptions[] MapMqttServers { get; set; }
}

public class MapMqttServerOptions
{
    public string Address { get; set; }
    public int Port { get; set; } = 1883;
    public string User { get; set; }
    public string Password { get; set; }
    public bool UseTls { get; set; }
    public bool AllowUntrustedCertificates { get; set; }
    public string TopicPrefix { get; set; }
    public bool UplinkEnabled { get; set; }
    public bool AnalyticsDownlinkEnabled { get; set; }
}
