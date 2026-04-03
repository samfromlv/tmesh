using Linux.Bluetooth;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TBot.Bot
{
    public class AdminBotService(
        TelegramBotClient botClient,
        IOptions<TBotOptions> options,
        BotCache botCache,
        RegistrationService registrationService,
        MeshtasticService meshtasticService)
    {

        private readonly TBotOptions _options = options.Value;

        public async Task<TgResult> HandleAdmin(
            long userId,
            long chatId,
            string text)
        {
            var chatStateWithData = registrationService.GetChatState(userId, chatId);
            var chatState = chatStateWithData?.State;

            var noPrefix = text["/admin".Length..].Trim();
            var segments = noPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                if (chatState == ChatState.Admin)
                {
                    await botClient.SendMessage(chatId, "Invalid admin command");
                    return TgResult.Ok;
                }
                else
                {
                    return TgResult.NotHandled;
                }
            }
            else if (chatState != ChatState.Admin && segments[0] != "login")
            {
                return TgResult.NotHandled;
            }

            var command = segments[0].ToLowerInvariant();

            switch (command)
            {
                case "login":
                    {
                        return await Login(userId, chatId, chatState, segments);
                    }
                case "logout":
                case "exit":
                case "quit":
                case "stop":
                    {
                        return await Logout(userId, chatId);
                    }
                case "public_text_primary":
                    {
                        return await PublicTextPrimary(chatId, noPrefix);
                    }
                case "public_text":
                    {
                        return await PublicText(chatId, noPrefix);
                    }
                case "text":
                    {
                        return await DirectText(chatId, noPrefix, segments);
                    }

                case "remove_node":
                    {
                        return await RemoveDevice(chatId, segments);
                    }

                case "add_gateway":
                    {
                        return await AddGateway(chatId, segments);
                    }
                case "remove_gateway":
                    {
                        return await RemoveGateway(chatId, segments);
                    }
                case "list_gateways":
                    {
                        return await ListGateways(chatId);
                    }
                case "list_networks":
                    {
                        return await ListNetworks(chatId);
                    }
                case "add_network":
                    {
                        return await AddNetwork(chatId, segments);
                    }
                case "update_network":
                    {
                        return await UpdateNetwork(chatId, segments);
                    }
                case "remove_network":
                    {
                        return await RemoveNetwork(chatId, segments);
                    }
                case "add_public_channel":
                    {
                        return await AddPublicChannel(chatId, segments);
                    }
                case "remove_public_channel":
                    {
                        return await RemovePublicChannel(chatId, segments);
                    }
                case "nodeinfo":
                    {
                        return await ShowNodeInfo(chatId, segments);
                    }

                default:
                    {
                        await botClient.SendMessage(chatId, $"Unknown admin command: {command}");
                        return TgResult.Ok;
                    }
            }
        }

        private async Task<TgResult> ListNetworks(long chatId)
        {
            var networks = await registrationService.GetNetworksCached();
            if (networks.Count == 0)
            {
                await botClient.SendMessage(chatId, "No networks configured.");
                return TgResult.Ok;
            }

            var gateways = await registrationService.GetGatewaysCached();
            var sb = new StringBuilder();
            sb.AppendLine("🌐 *Networks:*");

            foreach (var network in networks)
            {
                sb.AppendLine();
                var urlPart = network.Url != null ? $" - {network.Url}" : string.Empty;
                sb.AppendLine($"*\\[{network.Id}] {StringHelper.EscapeMd(network.Name)}* (`{StringHelper.EscapeMd(network.ShortName)}`){StringHelper.EscapeMd(urlPart)}");
                sb.AppendLine($"  sort: `{network.SortOrder}` · analytics: `{network.SaveAnalytics}` · disablepongs: `{network.DisablePongs}`");

                var publicChannels = await registrationService.GetPublicChannelsByNetworkAsync(network.Id);
                if (publicChannels.Count == 0)
                {
                    sb.AppendLine("  _No public channels_");
                }
                else
                {
                    foreach (var ch in publicChannels)
                    {
                        var primaryMark = ch.IsPrimary ? " ⭐" : "  ";
                        sb.AppendLine($"{primaryMark} ch\\#{ch.Id} `{StringHelper.EscapeMd(ch.Name)}`");
                    }
                }

                var networkGateways = gateways.Values.Where(g => g.NetworkId == network.Id).ToList();
                if (networkGateways.Count > 0)
                {
                    sb.AppendLine("  📡 _Gateways:_");
                    foreach (var gw in networkGateways)
                    {
                        var device = await registrationService.GetDeviceAsync(gw.DeviceId);
                        var hexId = MeshtasticService.GetMeshtasticNodeHexId(gw.DeviceId);
                        var lastSeen = gw.LastSeen.HasValue ? gw.LastSeen.Value.ToString("yyyy-MM-dd HH:mm") : "never";
                        sb.AppendLine($"  • {StringHelper.EscapeMd(device?.NodeName ?? hexId)} `{hexId}` - seen: {lastSeen}");
                    }
                }
                else
                {
                    sb.AppendLine("  📡 _No gateways_");
                }
            }

            await botClient.SendMessage(chatId, sb.ToString().TrimEnd(), parseMode: ParseMode.Markdown);
            return TgResult.Ok;
        }

        private async Task<TgResult> AddNetwork(long chatId, string[] segments)
        {
            // Usage: add_network "<name>" (<shortname> or - for null)  [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>]
            
            // Reconstruct the full command from segments to properly parse quoted strings
            var fullCommand = string.Join(' ', segments);
            
            // Try to extract the name from quotes
            var nameStartIdx = fullCommand.IndexOf('"');
            if (nameStartIdx == -1)
            {
                await botClient.SendMessage(chatId, "Usage: add_network \"<name>\" (<shortname> or - for null) [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>]\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false\nNote: Network name must be enclosed in double quotes.");
                return TgResult.Ok;
            }

            var nameEndIdx = fullCommand.IndexOf('"', nameStartIdx + 1);
            if (nameEndIdx == -1)
            {
                await botClient.SendMessage(chatId, "Invalid command: Network name must be enclosed in double quotes.\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false");
                return TgResult.Ok;
            }

            var name = fullCommand.Substring(nameStartIdx + 1, nameEndIdx - nameStartIdx - 1);
            
            // Parse the remaining arguments after the quoted name
            var remainingCommand = fullCommand[(nameEndIdx + 1)..].Trim();
            var remainingSegments = remainingCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            if (remainingSegments.Length < 1)
            {
                await botClient.SendMessage(chatId, "Usage: add_network \"<name>\" (<shortname> or - for null) [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>]\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false\nNote: shortname is required.");
                return TgResult.Ok;
            }

            var shortName = remainingSegments[0];
            var sortOrder = remainingSegments.Length >= 2 && int.TryParse(remainingSegments[1], out var so) ? so : 0;
            var saveAnalytics = remainingSegments.Length >= 3 && bool.TryParse(remainingSegments[2], out var sa) && sa;

            string url = null;
            bool disablePongs = false;

            foreach (var seg in remainingSegments.Skip(3))
            {
                var eqIdx = seg.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = seg[..eqIdx].ToLowerInvariant();
                var value = seg[(eqIdx + 1)..];
                switch (key)
                {
                    case "url":
                        url = value;
                        break;
                    case "disablepongs":
                        if (bool.TryParse(value, out var dp))
                            disablePongs = dp;
                        break;
                }
            }

            var network = await registrationService.AddNetwork(new Network
            {
                Name = name,
                ShortName = shortName == "-" ? null : shortName,
                SortOrder = sortOrder,
                SaveAnalytics = saveAnalytics,
                Url = url,
                DisablePongs = disablePongs
            });

            await botClient.SendMessage(chatId, $"Network added: [{network.Id}] {network.Name} (short: {network.ShortName}, analytics: {network.SaveAnalytics}, disablepongs: {network.DisablePongs}, url: {network.Url ?? "-"})");
            return new TgResult
            {
                Handled = true,
                NetworksUpdated = true,
                NetworkWithUpdatedPublicChannels = new List<int> { network.Id }
            };
        }

        private async Task<TgResult> UpdateNetwork(long chatId, string[] segments)
        {
            // Usage: update_network <id> [name=<value>] [shortname=<value>] [analytics=<true|false>] [url=<value>] [disablepongs=<true|false>]
            if (segments.Length < 3)
            {
                await botClient.SendMessage(chatId,
                    "Usage: update_network <id> [name=<value>] [shortname=<value>] [analytics=<true|false>] [url=<value>] [disablepongs=<true|false>]\n" +
                    "Example: update_network 1 name=NewName shortname=NN analytics=true url=https://example.com disablepongs=false\n" +
                    "To clear url, use url=-\n" +
                    "At least one field to update must be provided.");
                return TgResult.Ok;
            }

            if (!int.TryParse(segments[1], out var networkId))
            {
                await botClient.SendMessage(chatId, "Invalid network ID.");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId} not found.");
                return TgResult.Ok;
            }

            string newName = null;
            string newShortName = null;
            bool? newAnalytics = null;
            string newUrl = null;
            bool? newDisablePongs = null;

            foreach (var seg in segments.Skip(2))
            {
                var eqIdx = seg.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = seg[..eqIdx].ToLowerInvariant();
                var value = seg[(eqIdx + 1)..];
                switch (key)
                {
                    case "name":
                        newName = value;
                        break;
                    case "shortname":
                        newShortName = value;
                        break;
                    case "analytics":
                        if (bool.TryParse(value, out var analytics))
                            newAnalytics = analytics;
                        break;
                    case "url":
                        newUrl = value;
                        break;
                    case "disablepongs":
                        if (bool.TryParse(value, out var dp))
                            newDisablePongs = dp;
                        break;
                }
            }

            if (newName == null && newShortName == null && newAnalytics == null && newUrl == null && newDisablePongs == null)
            {
                await botClient.SendMessage(chatId,
                    "No valid fields provided. Use name=<value>, shortname=<value>, analytics=<true|false>, url=<value>, or disablepongs=<true|false>.");
                return TgResult.Ok;
            }

            await registrationService.UpdateNetworkAsync(networkId, newName, newShortName, newAnalytics, newUrl, newDisablePongs);

            var updated = await registrationService.GetNetwork(networkId);
            await botClient.SendMessage(chatId,
                $"Network updated: [{updated.Id}] {updated.Name} (short: {updated.ShortName}, sort: {updated.SortOrder}, analytics: {updated.SaveAnalytics}, disablepongs: {updated.DisablePongs}, url: {updated.Url ?? "-"})");

            return new TgResult
            {
                Handled = true,
                NetworksUpdated = true
            };
        }

        private async Task<TgResult> RemoveNetwork(long chatId, string[] segments)
        {
            // Usage: remove_network <id>
            if (segments.Length < 2 || !int.TryParse(segments[1], out var networkId))
            {
                await botClient.SendMessage(chatId, "Usage: remove_network <id>\nExample: remove_network 2");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId} not found.");
                return TgResult.Ok;
            }

            var removed = await registrationService.TryRemoveNetwork(networkId);
            if (removed)
            {
                await botClient.SendMessage(chatId, $"Network [{networkId}] \"{network.Name}\" removed.");
                return new TgResult
                {
                    Handled = true,
                    NetworksUpdated = true,
                    NetworkWithUpdatedPublicChannels = new List<int> { network.Id }
                };
            }
            else
            {
                await botClient.SendMessage(chatId,
                    $"Cannot remove network [{networkId}] \"{network.Name}\": it still has registered devices, channels, or gateways. Remove them first.");
                return TgResult.Ok;
            }

        }

        private async Task<TgResult> AddPublicChannel(long chatId, string[] segments)
        {
            // Usage: add_public_channel <networkId> <n> <key_hex> [primary]
            if (segments.Length < 5)
            {
                await botClient.SendMessage(chatId,
                    "Usage: add_public_channel <networkId> <n> <key_hex> [primary/secondary]\n" +
                    "key_base64: channel key as base64 (16 or 32 bytes)\n" +
                    "Example: add_public_channel 1 LongFast AQ== primary");
                return TgResult.Ok;
            }

            if (!int.TryParse(segments[1], out var networkId))
            {
                await botClient.SendMessage(chatId, "Invalid network ID.");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId} not found.");
                return TgResult.Ok;
            }

            var channelName = segments[2];
            var keyBase64 = segments[3];

            if (!MeshtasticService.TryParseChannelKey(keyBase64, out var key))
            {
                await botClient.SendMessage(chatId, $"Invalid key format: '{keyBase64}'. The channel key must be a valid Meshtastic channel key (base64-encoded, 16 or 32 bytes).");
                return TgResult.Ok;
            }
            var channelType = segments[4].ToLowerInvariant();

            if (!channelType.Equals("primary", StringComparison.OrdinalIgnoreCase)
                && !channelType.Equals("secondary", StringComparison.OrdinalIgnoreCase))
            {
                await botClient.SendMessage(chatId, $"Invalid channel type: '{segments[4]}'. The channel type must be either 'primary' or 'secondary'.");
                return TgResult.Ok;
            }

            var isPrimary = channelType.Equals("primary", StringComparison.OrdinalIgnoreCase);

            // Check for duplicates in this network
            var existingChannels = await registrationService.GetPublicChannelsByNetworkAsync(networkId);

            // Check for case-sensitive name duplicate
            var nameDuplicate = existingChannels.FirstOrDefault(c => c.Name == channelName && c.Key.SequenceEqual(key));
            if (nameDuplicate != null)
            {
                await botClient.SendMessage(chatId, $"A channel with the name '{channelName}' and key already exists in this network.");
                return TgResult.Ok;
            }

            // Check if trying to add a second primary channel
            if (isPrimary && existingChannels.Any(c => c.IsPrimary))
            {
                var primaryChannel = existingChannels.First(c => c.IsPrimary);
                await botClient.SendMessage(chatId, $"Cannot add a second primary channel. Channel #{primaryChannel.Id} \"{primaryChannel.Name}\" is already marked as primary in this network.");
                return TgResult.Ok;
            }

            var ch = await registrationService.AddPublicChannelAsync(networkId, channelName, key, isPrimary);
            var primaryMark = isPrimary ? " [primary]" : string.Empty;
            await botClient.SendMessage(chatId,
                $"Public channel added: #{ch.Id} \"{ch.Name}\"{primaryMark} → network [{networkId}] \"{network.Name}\"");

            return new TgResult
            {
                Handled = true,
                NetworkWithUpdatedPublicChannels = new List<int> { networkId }
            };
        }

        private async Task<TgResult> RemovePublicChannel(long chatId, string[] segments)
        {
            // Usage: remove_public_channel <id>
            if (segments.Length < 2 || !int.TryParse(segments[1], out var channelId))
            {
                await botClient.SendMessage(chatId, "Usage: remove_public_channel <id>\nExample: remove_public_channel 3");
                return TgResult.Ok;
            }

            var ch = await registrationService.GetPublicChannelByIdAsync(channelId);
            if (ch == null)
            {
                await botClient.SendMessage(chatId, $"Public channel with ID {channelId} not found.");
                return TgResult.Ok;
            }

            var (found, _) = await registrationService.RemovePublicChannelAsync(channelId);
            if (found)
            {
                var network = await registrationService.GetNetwork(ch.NetworkId);
                await botClient.SendMessage(chatId, $"Public channel #{channelId} \"{ch.Name}\" removed from network [{network.Id}] \"{network.Name}\".");
            }
            else
            {
                await botClient.SendMessage(chatId, $"Public channel with ID {channelId} not found.");
            }

            return new TgResult
            {
                Handled = true,
                NetworkWithUpdatedPublicChannels = new List<int> { ch.NetworkId }
            };
        }

        private async Task<TgResult> ShowNodeInfo(long chatId, string[] segments)
        {
            var nodeId = segments.Length >= 2 ? segments[1] : string.Empty;

            if (string.IsNullOrWhiteSpace(nodeId)
                || !MeshtasticService.TryParseDeviceId(nodeId, out var parsedNodeId))
            {
                await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }

            var device = await registrationService.GetDeviceAsync(parsedNodeId);
            if (device == null)
            {
                await botClient.SendMessage(chatId, "Node not found.");
                return TgResult.Ok;
            }

            var hexId = MeshtasticService.GetMeshtasticNodeHexId(parsedNodeId);
            var registrations = await registrationService.GetChatsByDeviceIdCached(device.DeviceId);
            var json = JsonSerializer.Serialize(device, TgBotService.IdentedOptions);

            var sb = new StringBuilder();
            sb.AppendLine($"📟 *{StringHelper.EscapeMd(device.NodeName)}* `{hexId}`");
            sb.AppendLine($"  Registrations: `{registrations.Count}`");
            sb.AppendLine();
            sb.AppendLine($"```");
            sb.AppendLine(json);
            sb.AppendLine($"```");

            await botClient.SendMessage(chatId, sb.ToString().TrimEnd(), parseMode: ParseMode.Markdown);
            return TgResult.Ok;
        }

        private async Task<TgResult> ListGateways(long chatId)
        {
            var networks = await registrationService.GetNetworksCached();
            var gateways = await registrationService.GetGatewaysCached();

            var sb = new StringBuilder();
            sb.AppendLine("📡 *Registered gateways:*");

            foreach (var network in networks)
            {
                var networkGateways = gateways.Values.Where(g => g.NetworkId == network.Id).ToList();
                sb.AppendLine();
                sb.AppendLine($"*{StringHelper.EscapeMd(network.Name)}* (ID `{network.Id}`)");
                if (networkGateways.Any())
                {
                    foreach (var gw in networkGateways)
                    {
                        var device = await registrationService.GetDeviceAsync(gw.DeviceId);
                        var hexId = MeshtasticService.GetMeshtasticNodeHexId(gw.DeviceId);
                        var lastSeen = gw.LastSeen.HasValue ? gw.LastSeen.Value.ToString("yyyy-MM-dd HH:mm:ss") : "never";
                        sb.AppendLine($"  • *{StringHelper.EscapeMd(device?.NodeName ?? hexId)}* `{hexId}` - seen: {lastSeen}");
                    }
                }
                else
                {
                    sb.AppendLine("  _No gateways registered_");
                }
            }

            await botClient.SendMessage(chatId, sb.ToString().TrimEnd(), parseMode: ParseMode.Markdown);
            return TgResult.Ok;
        }

        private async Task<TgResult> RemoveGateway(long chatId, string[] segments)
        {
            var nodeId = segments.Length >= 2 ? segments[1] : string.Empty;

            if (string.IsNullOrWhiteSpace(nodeId)
               || !MeshtasticService.TryParseDeviceId(nodeId, out var parsedNodeId))
            {
                await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }
            var hexId = MeshtasticService.GetMeshtasticNodeHexId(parsedNodeId);
            var device = await registrationService.GetDeviceAsync(parsedNodeId);
            var removed = await registrationService.UnregisterGatewayAsync(parsedNodeId);
            if (removed)
            {
                await botClient.SendMessage(chatId, $"Removed gateway {device?.NodeName ?? hexId}.");
            }
            else
            {
                await botClient.SendMessage(chatId, $"Gateway {device?.NodeName ?? hexId} was not registered.");
            }

            return new TgResult(removed && device != null ? [device.NetworkId] : null);
        }

        private async Task<TgResult> AddGateway(long chatId, string[] segments)
        {
            var nodeId = segments.Length >= 2 ? segments[1] : string.Empty;

            if (string.IsNullOrWhiteSpace(nodeId)
               || !MeshtasticService.TryParseDeviceId(nodeId, out var parsedNodeId))
            {
                await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #)." +
                    $"Example: /admin add_gateway <nodeId> <networkId>");
                return TgResult.Ok;
            }

            var networkId = segments.Length >= 3 && int.TryParse(segments[2], out var parsedNetworkId)
                ? parsedNetworkId
                : (int?)null;

            if (networkId == null)
            {
                await botClient.SendMessage(chatId, $"Invalid or missing network ID. Please specify a valid integer network ID as the second argument." +
                    $"Example: /admin add_gateway <nodeId> <networkId>");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId.Value);
            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId.Value} not found.");
                return TgResult.Ok;
            }

            var hexId = MeshtasticService.GetMeshtasticNodeHexId(parsedNodeId);
            var device = await registrationService.GetDeviceAsync(parsedNodeId);
            if (device != null && device.NetworkId != networkId.Value)
            {
                await botClient.SendMessage(chatId, $"Device {device.NodeName} is already registered to a different network - {device.NetworkId}.");
                return TgResult.Ok;
            }

            var gateways = await registrationService.GetGatewaysCached();
            var existingGatewayRegistration = gateways.GetValueOrDefault(parsedNodeId);
            if (existingGatewayRegistration != null && existingGatewayRegistration.NetworkId != networkId)
            {
                await botClient.SendMessage(chatId, $"Gateway {device?.NodeName ?? hexId} is already registered in a different network - [{existingGatewayRegistration.NetworkId}].");
                return TgResult.Ok;
            }


            var pwd = registrationService.DeriveMqttPasswordForDevice(parsedNodeId);

            await registrationService.RegisterGatewayAsync(parsedNodeId, networkId.Value);
            botCache.StoreGatewayRegistraionChat(parsedNodeId, chatId);
            var mqttUsername = hexId;
            var mqttPassword = registrationService.DeriveMqttPasswordForDevice(parsedNodeId);
            var mqttAddress = _options.PublicMqttAddress;
            var mqttTopic = _options.PublicMqttTopic.Replace(TgCommandBotService.NetworkIdToken, MqttService.NetworkSegmentPrefix + networkId.Value.ToString());
            var instructions = TgCommandBotService.CreateGatewaySetupInstructions(
                hexId,
                device?.NodeName,
                mqttUsername,
                mqttPassword,
                mqttAddress,
                mqttTopic,
                _options.PublicFlasherAddress,
                network.SaveAnalytics,
                _options.MeshtasticNodeNameLong,
                includeInfoAboutFirstSeenMessage: false);


            if (existingGatewayRegistration != null)
            {
                instructions.Insert(0, $"Gateway *{StringHelper.EscapeMd(device?.NodeName ?? hexId)}* is already registered in this network.\r\n\r\n");
            }

            await botClient.SendMessage(chatId, instructions.ToString(), parseMode: ParseMode.Markdown);

            return new TgResult
            {
                Handled = true,
                NetworkWithUpdatedGateways = [networkId.Value]
            };
        }

        private async Task<TgResult> RemoveDevice(long chatId, string[] segments)
        {
            var nodeId = segments.Length >= 2 ? segments[1] : string.Empty;

            if (string.IsNullOrWhiteSpace(nodeId)
                || !MeshtasticService.TryParseDeviceId(nodeId, out var parsedNodeId))
            {
                await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }

            var device = await registrationService.GetDeviceAsync(parsedNodeId);
            if (device == null)
            {
                await botClient.SendMessage(chatId, "Not found.");
                return TgResult.Ok;
            }

            await registrationService.DeleteDeviceAsync(device.DeviceId);

            await botClient.SendMessage(chatId, $"Deleted device {device.NodeName} ({device.DeviceId})");
            return TgResult.Ok;
        }

        private async Task<TgResult> DirectText(long chatId, string noPrefix, string[] segments)
        {
            var toDeviceID = segments.Length >= 2 ? segments[1] : string.Empty;
            var announcement = noPrefix[("text " + toDeviceID).Length..].Trim();
            if (string.IsNullOrWhiteSpace(toDeviceID))
            {
                await botClient.SendMessage(chatId, "Please specify the target device ID.");
                return TgResult.Ok;
            }
            if (!MeshtasticService.TryParseDeviceId(toDeviceID, out var parsedDeviceId))
            {
                await botClient.SendMessage(chatId, $"Invalid device ID format: '{toDeviceID}'. The device ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }
            if (string.IsNullOrWhiteSpace(announcement))
            {
                await botClient.SendMessage(chatId, "Message text cannot be empty.");
                return TgResult.Ok;
            }
            if (!MeshtasticService.CanSendMessage(announcement))
            {
                await botClient.SendMessage(
                    chatId,
                    $"Message is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).");
                return TgResult.Ok;
            }
            var device = await registrationService.GetDeviceAsync(parsedDeviceId);
            if (device == null)
            {
                await botClient.SendMessage(chatId, $"Device {toDeviceID} not found.");
                return TgResult.Ok;
            }

            var msg = await botClient.SendMessage(chatId, $"Sending message to device {toDeviceID}...");

            return new TgResult(new OutgoingTextMessage
            {
                Recipient = device,
                Text = announcement,
                TelegramChatId = chatId,
                TelegramMessageId = msg.Id
            });
        }

        private async Task<TgResult> PublicText(long chatId, string noPrefix)
        {
            var cmd = noPrefix["public_text".Length..].Trim();
            var networkIdStr = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var channelNameEndIndex = cmd.IndexOf(' ');

            if (channelNameEndIndex == -1)
            {
                await botClient.SendMessage(chatId, "Usage: public_text <networkId> <channelName> <text>\nPlease specify the network ID, channel name, and announcement text.");
                return TgResult.Ok;
            }

            if (!int.TryParse(networkIdStr, out var networkId))
            {
                await botClient.SendMessage(chatId, "Invalid network ID. Please specify a valid integer network ID.");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId} not found.");
                return TgResult.Ok;
            }

            var remainingCmd = cmd[(networkIdStr.Length + 1)..].Trim();
            var channelNameEnd = remainingCmd.IndexOf(' ');
            if (channelNameEnd == -1)
            {
                await botClient.SendMessage(chatId, "Please specify the channel name and announcement text.");
                return TgResult.Ok;
            }

            var channelName = remainingCmd[..channelNameEnd].Trim();
            var announcement = remainingCmd[channelNameEnd..].Trim();

            if (string.IsNullOrWhiteSpace(announcement))
            {
                await botClient.SendMessage(chatId, "Announcement text cannot be empty.");
                return TgResult.Ok;
            }

            if (!MeshtasticService.CanSendMessage(announcement))
            {
                await botClient.SendMessage(
                    chatId,
                    $"Announcement is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).");
                return TgResult.Ok;
            }

            // Get the public channel by name in the specified network
            var publicChannels = await registrationService.GetPublicChannelsByNetworkAsync(networkId);
            var channel = publicChannels.FirstOrDefault(c => c.Name == channelName);

            if (channel == null)
            {
                await botClient.SendMessage(chatId, $"Channel '{channelName}' is not found in network [{networkId}] \"{network.Name}\".");
                return TgResult.Ok;
            }

            meshtasticService.SendPublicTextMessage(
                announcement,
                relayGatewayId: null,
                hopLimit: int.MaxValue,
                publicChannelName: channelName,
                recipient: channel);

            await botClient.SendMessage(chatId, $"Announcement sent to channel '{channelName}' in network [{networkId}] \"{network.Name}\".");
            return TgResult.Ok;
        }

        private async Task<TgResult> PublicTextPrimary(long chatId, string noPrefix)
        {
            var cmd = noPrefix["public_text_primary".Length..].Trim();
            var networkIdStr = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(networkIdStr) || !int.TryParse(networkIdStr, out var networkId))
            {
                await botClient.SendMessage(chatId, "Usage: public_text_primary <networkId> <text>\nPlease specify the network ID.");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId} not found.");
                return TgResult.Ok;
            }

            var announcement = cmd[(networkIdStr.Length + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(announcement))
            {
                await botClient.SendMessage(chatId, "Announcement text cannot be empty.");
                return TgResult.Ok;
            }
            if (!MeshtasticService.CanSendMessage(announcement))
            {
                await botClient.SendMessage(
                    chatId,
                    $"Announcement is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).");
                return TgResult.Ok;
            }

            // Get the primary channel in the specified network
            var publicChannels = await registrationService.GetPublicChannelsByNetworkAsync(networkId);
            var primaryChannel = publicChannels.FirstOrDefault(c => c.IsPrimary);

            if (primaryChannel == null)
            {
                await botClient.SendMessage(chatId, $"No primary channel is configured in network [{networkId}] \"{network.Name}\".");
                return TgResult.Ok;
            }

            meshtasticService.SendPublicTextMessage(
                announcement,
                relayGatewayId: null,
                hopLimit: int.MaxValue,
                publicChannelName: primaryChannel.Name,
                recipient: primaryChannel);

            await botClient.SendMessage(chatId, $"Announcement sent to primary channel in network [{networkId}] \"{network.Name}\".");
            return TgResult.Ok;
        }

        private async Task<TgResult> Logout(long userId, long chatId)
        {
            registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
            await botClient.SendMessage(chatId, "Admin access revoked.");
            return TgResult.Ok;
        }

        private async Task<TgResult> Login(long userId, long chatId, ChatState? chatState, string[] segments)
        {
            if (chatState == ChatState.Admin)
            {
                await botClient.SendMessage(chatId, "You are already logged in as admin.");
                return TgResult.Ok;
            }
            var password = segments.Length >= 2 ? segments[1] : string.Empty;
            if (registrationService.TryAdminLogin(userId, password))
            {
                registrationService.SetChatState(userId, chatId, Models.ChatState.Admin);
                await botClient.SendMessage(chatId, "Admin access granted.");
            }
            else
            {
                await botClient.SendMessage(chatId, "Invalid admin password.");
            }
            return TgResult.Ok;
        }
    }
}
