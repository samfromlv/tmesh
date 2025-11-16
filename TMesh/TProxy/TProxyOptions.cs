namespace TProxy;

public class TProxyOptions
{
    public string MqttAddress { get; set; } 
    public int MqttPort { get; set; }
    public string MqttUser { get; set; }
    public string MqttPassword { get; set; }

    public bool MqttAllowUntrustedCertificates { get; set; }
    public bool MqttUseTls { get; set; }
    public string MqttTelegramTopic { get; set; }
    public string MqttStatusTopic { get; set; }

    // Telegram webhook security
    public string TelegramWebhookSecret { get; set; } // expected header value
    public bool DisableTelegramTokenValidation { get; set; } // set true in development to skip validation
}
