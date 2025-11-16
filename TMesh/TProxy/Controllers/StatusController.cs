using Microsoft.AspNetCore.Mvc;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TProxy.Controllers
{
    [ApiController]
    [Route("/status")]
    public class StatusController : Controller
    {
        private readonly MqttPublisher _publisher;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
        };

        public StatusController(MqttPublisher publisher)
        {
            _publisher = publisher;
        }

        [HttpGet("bot")]
        public IActionResult Bot()
        {
            var status = _publisher.LastStatusPayload;
            if (status == null)
            {
                return NotFound();
            }
            return Json(status, _jsonOptions);
        }

        [HttpGet("bot/health")]
        public IActionResult BotHealth()
        {
            var started = _publisher.Started;
            if ((DateTime.UtcNow - started).TotalMinutes < 5)
            {
                return Ok("Starting up");
            }

            var status = _publisher.LastStatusPayload;
            if (status == null)
            {
                return NotFound();
            }
            if (status.LastUpdate < DateTime.UtcNow.AddMinutes(-10))
            {
                return StatusCode(503, "No recent updates");
            }
            return Ok("Healthy");
        }
    }
}
