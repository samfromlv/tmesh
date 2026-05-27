using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Cors;

namespace TProxy.Controllers
{
    [ApiController]
    [Route("/status")]
    public class StatusController(MqttService publisher) : Controller
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        [HttpGet("bot")]
        public IActionResult Bot()
        {
            var status = publisher.LastStatusPayload;
            if (status == null)
            {
                return NotFound();
            }
            return Json(status, _jsonOptions);
        }

        [HttpGet("network/{id}")]
        public IActionResult Network(int id)
        {
            var status = publisher.LastStatusPayload;
            if (status == null)
            {
                return NotFound();
            }
            var networkStatus = status.Networks.FirstOrDefault(n => n.Id == id);
            if (networkStatus == null)
            {
                return NotFound();
            }
            return Json(networkStatus, _jsonOptions);
        }

        [HttpGet("votestat/{networkId}/{voteId}")]
        [EnableCors("AllowAll")]
        public IActionResult VoteStat(int networkId, int voteId)
        {
            var status = publisher.LastStatusPayload;
            if (status == null)
            {
                return NotFound();
            }
            var networkStatus = status.Networks.FirstOrDefault(n => n.Id == networkId);
            if (networkStatus == null)
            {
                return NotFound();
            }
            var voteStat = networkStatus.ActiveVotes.FirstOrDefault(v => v.VoteId == voteId);
            if (voteStat == null)
            {
                return NotFound();
            }
            return Json(new 
            {
                VoteId = voteId,
                LastUpdateTs = voteStat.LastUpdateTimestampSec,
                voteStat.Stats
            }, _jsonOptions);
        }

        [HttpGet("gateway/{id}")]
        public IActionResult GatewayHealth(string id)
        {
            var started = publisher.Started;
            if ((DateTime.UtcNow - started).TotalMinutes < 5)
            {
                return Ok("Starting up");
            }

            var status = publisher.LastStatusPayload;
            if (status == null)
            {
                return NotFound();
            }
            if (status == null || status.LastUpdate < DateTime.UtcNow.AddMinutes(-10))
            {
                return StatusCode(503, "No recent updates");
            }

            // Search for gateway in all networks
            DateTime? lastSeen = null;
            foreach (var network in status.Networks)
            {
                if (network.GatewaysLastSeen?.TryGetValue(id, out var networkLastSeen) == true)
                {
                    lastSeen = networkLastSeen;
                    break;
                }
            }
            if (lastSeen == null)
            {
                return NotFound("Gateway ID not found");
            }
            if (lastSeen < DateTime.UtcNow.AddMinutes(-60))
            {
                return StatusCode(503, $"Gateway offline. Last seen at {lastSeen.Value:u}");
            }
            return Ok("Healthy");
        }


        [HttpGet("bot/health")]
        public IActionResult BotHealth(int? networkId, int? gatewayDeadMinutes, string gatewayCheckMode = "all")
        {
            var started = publisher.Started;
            if ((DateTime.UtcNow - started).TotalMinutes < 5)
            {
                return Ok("Starting up");
            }

            var status = publisher.LastStatusPayload;
            if (status == null || status.LastUpdate < DateTime.UtcNow.AddMinutes(-10))
            {
                return StatusCode(503, "No recent updates");
            }

            // Collect all gateways from all networks
            var allGateways = status.Networks
                .Where(n => networkId == null || n.Id == networkId)
                .Where(n => n.GatewaysLastSeen != null)
                .SelectMany(n => n.GatewaysLastSeen.Values)
                .ToList();

            if (allGateways.Count == 0 || allGateways.All(x => x == null))
            {
                return StatusCode(503, "No gateways connected");
            }

            bool gatewayCheckAny = gatewayCheckMode.Equals("any", StringComparison.CurrentCultureIgnoreCase);
            var border = DateTime.UtcNow.AddMinutes(-1 * (gatewayDeadMinutes ?? 60));

            if (gatewayCheckAny && allGateways.Any(t => t < border))
            {
                var unhealthCount = allGateways.Count(t => t < border);
                return StatusCode(503, $"Some gateways offline - {unhealthCount}");
            }
            else if (!gatewayCheckAny && allGateways.All(t => t < border))
            {
                return StatusCode(503, "All gateways offline");
            }
            return Ok("Healthy");
        }

    }
}
