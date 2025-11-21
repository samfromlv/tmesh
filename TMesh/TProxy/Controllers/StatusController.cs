using Microsoft.AspNetCore.Mvc;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TProxy.Controllers
{
    [ApiController]
    [Route("/status")]
    public class StatusController : Controller
    {
        private readonly MqttService _publisher;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
        };

        public StatusController(MqttService publisher)
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

        [HttpGet("gateway/{id}")]
        public IActionResult GatewayHealth(string id)
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
            if (status == null || status.LastUpdate < DateTime.UtcNow.AddMinutes(-10))
            {
                return StatusCode(503, "No recent updates");
            }

            if (!status.GatewaysLastSeen.ContainsKey(id))
            {
                return NotFound("Gateway ID not found");
            }
            var lastSeen = status.GatewaysLastSeen[id];
            if (lastSeen == null)
            {
                return StatusCode(503, "Gateway offline. No last activity");
            }
            if (lastSeen < DateTime.UtcNow.AddMinutes(-60))
            {
                return StatusCode(503, $"Gateway offline. Last seen at {lastSeen.Value:u}");
            }
            return Ok("Healthy");
        }


        [HttpGet("bot/health")]
        public IActionResult BotHealth(int? gatewayDeadMinutes, string gatewayCheckMode = "all")
        {
            var started = _publisher.Started;
            if ((DateTime.UtcNow - started).TotalMinutes < 5)
            {
                return Ok("Starting up");
            }

            var status = _publisher.LastStatusPayload;
            if (status == null || status.LastUpdate < DateTime.UtcNow.AddMinutes(-10))
            {
                return StatusCode(503, "No recent updates");
            }
            if (status.GatewaysLastSeen.Count == 0
                || status.GatewaysLastSeen.Values.All(x => x == null))
            {
                return StatusCode(503, "No gateways connected");
            }
            bool gatewayCheckAny = gatewayCheckMode.ToLower() == "any";
            var border = DateTime.UtcNow.AddMinutes(-1 * (gatewayDeadMinutes ?? 60));
            if (gatewayCheckAny && status.GatewaysLastSeen.Values.Any(t => t < border))
            {
                var unhealthCount = status.GatewaysLastSeen.Values.Count(t => t < border);
                return StatusCode(503, $"Some gateways offline - {unhealthCount}");
            }
            else if (!gatewayCheckAny && status.GatewaysLastSeen.Values.All(t => t < border))
            {
                return StatusCode(503, "All gateways offline");
            }
            return Ok("Healthy");
        }

    }
}
