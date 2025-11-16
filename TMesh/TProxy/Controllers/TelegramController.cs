using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace TProxy.Controllers
{
    [ApiController]
    [Route("")]
    public class TelegramController : ControllerBase
    {
        private readonly ILogger<TelegramController> _logger;
        private readonly MqttService _publisher;
        private readonly TProxyOptions _options;

        public TelegramController(ILogger<TelegramController> logger, MqttService publisher, Microsoft.Extensions.Options.IOptions<TProxyOptions> options)
        {
            _logger = logger;
            _publisher = publisher;
            _options = options.Value;
        }

        [HttpGet("")]
        public IActionResult Index()
        {
            return Ok("TProxy Telegram Webhook is running.");
        }

        [HttpPost("update")]
        public async Task<IActionResult> Update()
        {
            // Validate Telegram secret token header unless disabled
            if (!_options.DisableTelegramTokenValidation)
            {
                if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var provided) || string.IsNullOrEmpty(_options.TelegramWebhookSecret) || !string.Equals(provided.ToString(), _options.TelegramWebhookSecret, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Rejected update due to missing/invalid Telegram secret token header.");
                    return Unauthorized();
                }
            }

            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                return BadRequest("Empty body");
            }

            // Validate JSON
            try
            {
                JsonDocument.Parse(body);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid JSON received");
                return BadRequest("Invalid JSON");
            }

            var topic = _options.MqttTelegramTopic;
            try
            {
                await _publisher.PublishAsync(topic, body, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish MQTT message");
                return StatusCode(500, "Failed to publish");
            }

            return Ok();
        }
    }
}
