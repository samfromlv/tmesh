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
        MeshtasticService meshtasticService,
        TimeZoneHelper timeZoneHelper)
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
                case "trace":
                    {
                        return await Trace(chatId, segments);
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
                case "update_public_channel":
                    {
                        return await UpdatePublicChannel(chatId, segments);
                    }
                case "remove_public_channel":
                    {
                        return await RemovePublicChannel(chatId, segments);
                    }
                case "add_scheduled_message":
                    {
                        return await AddScheduledMessage(chatId, noPrefix, segments);
                    }
                case "delete_scheduled_message":
                    {
                        return await DeleteScheduledMessage(chatId, segments);
                    }
                case "toggle_scheduled_message":
                    {
                        return await ToggleScheduledMessage(chatId, segments);
                    }
                case "list_scheduled_messages":
                    {
                        return await ListScheduledMessages(chatId);
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
                var urlPart = network.Url != null ? $" - {network.Url}" : " No URL";
                sb.AppendLine($"*\\[{network.Id}\\] {StringHelper.EscapeMdV2(network.Name)}* \\(`{StringHelper.EscapeMdV2(network.ShortName)}`\\){StringHelper.EscapeMdV2(urlPart)}");
                sb.AppendLine($"  sort: `{network.SortOrder}` · analytics: `{network.SaveAnalytics}` · disablepongs: `{network.DisablePongs}` · disablewelcome: `{network.DisableWelcomeMessage}`");
                var communityUrlPart = network.CommunityUrl != null ? $" · communityurl: `{StringHelper.EscapeMdV2(network.CommunityUrl)}`" : "No Community URL";
                if (!string.IsNullOrEmpty(communityUrlPart))
                {
                    sb.AppendLine($"  {communityUrlPart.TrimStart(new char[] { ' ', '·' })}");
                }

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
                        sb.AppendLine($"{primaryMark} ch\\#{ch.Id} `{StringHelper.EscapeMdV2(ch.Name)}`");
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
                        var lastSeen = gw.LastSeen.HasValue ? StringHelper.EscapeMdV2(timeZoneHelper.ConvertFromUtcToDefaultTimezone(gw.LastSeen.Value).ToString("yyyy-MM-dd HH:mm")) : "never";
                        sb.AppendLine($"  • {StringHelper.EscapeMdV2(device?.NodeName ?? hexId)} `{StringHelper.EscapeMdV2(hexId)}` \\- seen: {lastSeen}");
                    }
                }
                else
                {
                    sb.AppendLine("  📡 _No gateways_");
                }
            }

            await botClient.SendMessage(chatId, sb.ToString().TrimEnd(), parseMode: ParseMode.MarkdownV2);
            return TgResult.Ok;
        }

        private async Task<TgResult> AddNetwork(long chatId, string[] segments)
        {
            // Usage: add_network "<name>" (<shortname> or - for null)  [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [disablewelcomemessage=<true|false>".]

            // Reconstruct the full command from segments to properly parse quoted strings
            var fullCommand = string.Join(' ', segments);

            // Try to extract the name from quotes
            var nameStartIdx = fullCommand.IndexOf('"');
            if (nameStartIdx == -1)
            {
                await botClient.SendMessage(chatId, "Usage: add_network \"<name>\" (<shortname> or - for null) [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [disablewelcomemessage=<true|false>]\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false communityurl=https://community.com disablewelcomemessage=false\nNote: Network name must be enclosed in double quotes.");
                return TgResult.Ok;
            }

            var nameEndIdx = fullCommand.IndexOf('"', nameStartIdx + 1);
            if (nameEndIdx == -1)
            {
                await botClient.SendMessage(chatId, "Invalid command: Network name must be enclosed in double quotes.\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false communityurl=https://community.com disablewelcomemessage=false");
                return TgResult.Ok;
            }

            var name = fullCommand.Substring(nameStartIdx + 1, nameEndIdx - nameStartIdx - 1);

            // Parse the remaining arguments after the quoted name
            var remainingCommand = fullCommand[(nameEndIdx + 1)..].Trim();
            var remainingSegments = remainingCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (remainingSegments.Length < 1)
            {
                await botClient.SendMessage(chatId, "Usage: add_network \"<name>\" (<shortname> or - for null) [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [disablewelcomemessage=<true|false>]\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false communityurl=https://community.com disablewelcomemessage=false\nNote: shortname is required.");
                return TgResult.Ok;
            }

            var shortName = remainingSegments[0];
            var sortOrder = remainingSegments.Length >= 2 && int.TryParse(remainingSegments[1], out var so) ? so : 0;
            var saveAnalytics = remainingSegments.Length >= 3 && bool.TryParse(remainingSegments[2], out var sa) && sa;

            string url = null;
            bool disablePongs = false;
            string communityUrl = null;
            bool disableWelcomeMessage = false;

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
                    case "communityurl":
                        communityUrl = value;
                        break;
                    case "disablewelcomemessage":
                        if (bool.TryParse(value, out var dwm))
                            disableWelcomeMessage = dwm;
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
                DisablePongs = disablePongs,
                CommunityUrl = communityUrl,
                DisableWelcomeMessage = disableWelcomeMessage
            });

            await botClient.SendMessage(chatId, $"Network added: [{network.Id}] {network.Name} (short: {network.ShortName}, analytics: {network.SaveAnalytics}, disablepongs: {network.DisablePongs}, url: {network.Url ?? "-"}, communityurl: {network.CommunityUrl ?? "-"}, disablewelcomemessage: {network.DisableWelcomeMessage})");
            return new TgResult
            {
                Handled = true,
                NetworksUpdated = true,
                NetworkWithUpdatedPublicChannels = new List<int> { network.Id }
            };
        }

        private async Task<TgResult> UpdateNetwork(long chatId, string[] segments)
        {
            // Usage: update_network <id> [name=<value>] [shortname=<value>] [analytics=<true|false>] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [disablewelcomemessage=<true|false>]
            if (segments.Length < 3)
            {
                await botClient.SendMessage(chatId,
                    "Usage: update_network <id> [name=<value>] [shortname=<value>] [analytics=<true|false>] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [disablewelcomemessage=<true|false>]\n" +
                    "Example: update_network 1 name=NewName shortname=NN analytics=true url=https://example.com disablepongs=false communityurl=https://community.com disablewelcomemessage=false\n" +
                    "To clear url or communityurl, use url=- or communityurl=-\n" +
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
            string newCommunityUrl = null;
            bool? newDisableWelcomeMessage = null;

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
                    case "communityurl":
                        newCommunityUrl = value;
                        break;
                    case "disablewelcomemessage":
                        if (bool.TryParse(value, out var dwm))
                            newDisableWelcomeMessage = dwm;
                        break;
                }
            }

            if (newName == null && newShortName == null && newAnalytics == null && newUrl == null && newDisablePongs == null && newCommunityUrl == null && newDisableWelcomeMessage == null)
            {
                await botClient.SendMessage(chatId,
                    "No valid fields provided. Use name=<value>, shortname=<value>, analytics=<true|false>, url=<value>, disablepongs=<true|false>, communityurl=<value>, or disablewelcomemessage=<true|false>.");
                return TgResult.Ok;
            }

            await registrationService.UpdateNetworkAsync(networkId, newName, newShortName, newAnalytics, newUrl, newDisablePongs, newCommunityUrl, newDisableWelcomeMessage);

            var updated = await registrationService.GetNetwork(networkId);
            await botClient.SendMessage(chatId,
                $"Network updated: [{updated.Id}] {updated.Name} (short: {updated.ShortName}, sort: {updated.SortOrder}, analytics: {updated.SaveAnalytics}, disablepongs: {updated.DisablePongs}, url: {updated.Url ?? "-"}, communityurl: {updated.CommunityUrl ?? "-"}, disablewelcomemessage: {updated.DisableWelcomeMessage})");

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
            // Usage: add_public_channel <networkId> <n> <key_base64> primary
            //        add_public_channel <networkId> <n> <key_base64> secondary <send_node_info_on_secondary>
            if (segments.Length < 5)
            {
                await botClient.SendMessage(chatId,
                    "Usage: add_public_channel <networkId> <n> <key_base64> primary\n" +
                    "       add_public_channel <networkId> <n> <key_base64> secondary <send_node_info_on_secondary>\n" +
                    "key_base64: channel key as base64 (16 or 32 bytes)\n" +
                    "Example: add_public_channel 1 LongFast AQ== primary\n" +
                    "Example: add_public_channel 1 MediumSlow AQ== secondary true");
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

            bool sendNodeInfoOnSecondary = false;
            if (!isPrimary)
            {
                if (segments.Length < 6 || !bool.TryParse(segments[5], out sendNodeInfoOnSecondary))
                {
                    await botClient.SendMessage(chatId,
                        "For secondary channels, <send_node_info_on_secondary> is required (true or false).\n" +
                        "Example: add_public_channel 1 MediumSlow AQ== secondary false");
                    return TgResult.Ok;
                }
            }

            // Check for duplicates in this network
            var existingChannels = await registrationService.GetPublicChannelsByNetworkAsync(networkId);

            // Check for case-sensitive name+key duplicate
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

            var ch = await registrationService.AddPublicChannelAsync(networkId, channelName, key, isPrimary, sendNodeInfoOnSecondary);
            var primaryMark = isPrimary ? " [primary]" : $" [secondary, send_node_info={sendNodeInfoOnSecondary}]";
            await botClient.SendMessage(chatId,
                $"Public channel added: #{ch.Id} \"{ch.Name}\"{primaryMark} → network [{networkId}] \"{network.Name}\"");

            return new TgResult
            {
                Handled = true,
                NetworkWithUpdatedPublicChannels = new List<int> { networkId }
            };
        }

        private async Task<TgResult> UpdatePublicChannel(long chatId, string[] segments)
        {
            // Usage: update_public_channel <id> <is_primary> <send_node_info_on_secondary>
            if (segments.Length < 4
                || !int.TryParse(segments[1], out var channelId)
                || !bool.TryParse(segments[2], out var isPrimary)
                || !bool.TryParse(segments[3], out var sendNodeInfoOnSecondary))
            {
                await botClient.SendMessage(chatId,
                    "Usage: update_public_channel <id> <is_primary> <send_node_info_on_secondary>\n" +
                    "Example: update_public_channel 2 false true");
                return TgResult.Ok;
            }

            var ch = await registrationService.GetPublicChannelByIdAsync(channelId);
            if (ch == null)
            {
                await botClient.SendMessage(chatId, $"Public channel with ID {channelId} not found.");
                return TgResult.Ok;
            }

            (var ok, var updated) = await registrationService.TryUpdatePublicChannelAsync(
                channelId, 
                isPrimary,
                sendNodeInfoOnSecondary);

            if (!ok)
            {
                await botClient.SendMessage(chatId, $"Failed to update public channel with ID {channelId}. It may have been removed or you are trying to remove only primary channel.");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(updated.NetworkId);
            var primaryMark = updated.IsPrimary ? " [primary]" : $" [secondary, send_node_info={updated.SendNodeInfoOnSecondary}]";
            await botClient.SendMessage(chatId,
                $"Public channel updated: #{updated.Id} \"{updated.Name}\"{primaryMark} → network [{updated.NetworkId}] \"{network?.Name}\"");

            return new TgResult
            {
                Handled = true,
                NetworkWithUpdatedPublicChannels = new List<int> { updated.NetworkId }
            };
        }

        private async Task<TgResult> RemovePublicChannel(long chatId, string[] segments)
        {
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

        private async Task<TgResult> Trace(long chatId, string[] segments)
        {
            var nodeId = segments.Length >= 2 ? segments[1] : string.Empty;

            if (string.IsNullOrWhiteSpace(nodeId)
                || !MeshtasticService.TryParseDeviceId(nodeId, out var parsedNodeId))
            {
                await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }

            int? networkId = segments.Length >= 3 && int.TryParse(segments[2], out var parsedNetworkId) ? parsedNetworkId : null;

            var device = await registrationService.GetDeviceAsync(parsedNodeId);
            if (device == null && networkId == null)
            {
                await botClient.SendMessage(chatId, "Node not found and there is no network specified as second argument, please specify network for tracking unknown nodes (/trace !aabbccdd 1)");
                return TgResult.Ok;
            }

            if (device != null && networkId != null && device.NetworkId != networkId.Value)
            {
                await botClient.SendMessage(chatId, $"Node {device.NodeName} belongs to a different network [{device.NetworkId}] than the one specified in the command [{networkId.Value}]. Please check the network ID or omit it to trace the node in its registered network.");
                return TgResult.Ok;
            }

            if (networkId == null)
            {
                networkId = device.NetworkId;
            }

            var network = await registrationService.GetNetwork(networkId.Value);

            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId.Value} not found.");
                return TgResult.Ok;
            }

            var primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(networkId.Value);
            if (primaryChannel == null)
            {
                await botClient.SendMessage(chatId, $"The network {network.Name} does not have a primary channel configured. Please set up a primary channel for the network to enable tracing.");
                return TgResult.Ok;
            }

            var deviceGateway = botCache.GetDeviceGateway(parsedNodeId);

            var msgId = MeshtasticService.GetNextMeshtasticMessageId();

            botCache.StoreTraceRouteChat(msgId, chatId);

            meshtasticService.SendTraceRouteRequest(msgId, parsedNodeId, deviceGateway?.GatewayId, primaryChannel, primaryChannel.Name);

            var hexId = MeshtasticService.GetMeshtasticNodeHexId(parsedNodeId);

            await botClient.SendMessage(chatId, $"Started tracing node `{(device != null? device.NodeName + $" ({hexId})" : hexId)}` in network [{networkId.Value}] \"{network.Name}\". You will receive updates in this chat as the trace route progresses.");

            return TgResult.Ok;
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
                if (networkGateways.Count != 0)
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

        private static readonly string LocalDateFormat = "yyyy-MM-ddTHH:mm:ss";

        private bool TryParseLocalDate(string value, out DateTime result)
            => DateTime.TryParseExact(value, LocalDateFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result);

        private async Task<TgResult> AddScheduledMessage(long chatId, string noPrefix, string[] segments)
        {
            // Usage: add_scheduled_message <publicChannelId> <intervalMinutes> <message text> [enable_at=yyyy-MM-ddTHH:mm:ss] [disable_at=yyyy-MM-ddTHH:mm:ss]
            if (segments.Length < 4)
            {
                await botClient.SendMessage(chatId,
                    "Usage: add_scheduled_message <publicChannelId> <intervalMinutes> <message text> [enable_at=yyyy-MM-ddTHH:mm:ss] [disable_at=yyyy-MM-ddTHH:mm:ss]\n" +
                    "Example: add_scheduled_message 1 60 Hello mesh! enable_at=2025-06-01T08:00:00 disable_at=2025-09-01T00:00:00\n" +
                    "Dates are in local time. If enable_at is set the message starts disabled.");
                return TgResult.Ok;
            }

            if (!int.TryParse(segments[1], out var publicChannelId))
            {
                await botClient.SendMessage(chatId, "Invalid public channel ID.");
                return TgResult.Ok;
            }

            if (!int.TryParse(segments[2], out var intervalMinutes) || intervalMinutes < 1)
            {
                await botClient.SendMessage(chatId, "Invalid interval. Must be a positive integer number of minutes.");
                return TgResult.Ok;
            }

            var channel = await registrationService.GetPublicChannelByIdAsync(publicChannelId);
            if (channel == null)
            {
                await botClient.SendMessage(chatId, $"Public channel with ID {publicChannelId} not found.");
                return TgResult.Ok;
            }

            // Everything after "add_scheduled_message <id> <interval> "
            var afterFixed = noPrefix[$"add_scheduled_message {segments[1]} {segments[2]} ".Length..].Trim();

            // Peel off trailing key=value tokens (enable_at / disable_at) from the end
            DateTime? enableAtUtc = null;
            DateTime? disableAtUtc = null;
            var words = afterFixed.Split(' ');
            int textWordCount = words.Length;

            for (int i = words.Length - 1; i >= 1; i--)
            {
                var word = words[i];
                var eqIdx = word.IndexOf('=');
                if (eqIdx <= 0) break;
                var key = word[..eqIdx].ToLowerInvariant();
                var val = word[(eqIdx + 1)..];

                if (key == "enable_at" || key == "disable_at")
                {
                    if (!TryParseLocalDate(val, out var localDt))
                    {
                        await botClient.SendMessage(chatId,
                            $"Invalid date format for {key}: '{val}'. Required format: {LocalDateFormat}");
                        return TgResult.Ok;
                    }
                    var utcDt = timeZoneHelper.ConvertFromDefaultTimezoneToUtc(localDt);
                    if (key == "enable_at") enableAtUtc = utcDt;
                    else disableAtUtc = utcDt;
                    textWordCount = i;
                }
                else
                {
                    break;
                }
            }

            var text = string.Join(' ', words[..textWordCount]).Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, "Message text cannot be empty.");
                return TgResult.Ok;
            }

            if (!MeshtasticService.CanSendMessage(text))
            {
                await botClient.SendMessage(chatId,
                    $"Message is too long. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes.");
                return TgResult.Ok;
            }

            var msg = await registrationService.AddScheduledMessageAsync(publicChannelId, intervalMinutes, text, enableAtUtc, disableAtUtc);

            var sb = new StringBuilder();
            sb.AppendLine($"Scheduled message #{msg.Id} added: every {intervalMinutes} min → channel #{publicChannelId} \"{StringHelper.EscapeMd(channel.Name)}\"");
            sb.AppendLine($"Status: {(msg.Enabled ? "enabled ✅" : "disabled ⏸ (starts when enable\\_at is reached)")}");
            if (enableAtUtc.HasValue)
                sb.AppendLine($"Enable at: `{StringHelper.EscapeMd(timeZoneHelper.ConvertFromUtcToDefaultTimezone(enableAtUtc.Value).ToString(LocalDateFormat))}` (local)");
            if (disableAtUtc.HasValue)
                sb.AppendLine($"Disable at: `{StringHelper.EscapeMd(timeZoneHelper.ConvertFromUtcToDefaultTimezone(disableAtUtc.Value).ToString(LocalDateFormat))}` (local)");
            sb.AppendLine($"Text: {StringHelper.EscapeMd(text)}");

            await botClient.SendMessage(chatId, sb.ToString().TrimEnd(), parseMode: ParseMode.Markdown);
            return TgResult.Ok;
        }

        private async Task<TgResult> DeleteScheduledMessage(long chatId, string[] segments)
        {
            if (segments.Length < 2 || !int.TryParse(segments[1], out var messageId))
            {
                await botClient.SendMessage(chatId, "Usage: delete_scheduled_message <messageId>");
                return TgResult.Ok;
            }

            var deleted = await registrationService.DeleteScheduledMessageAsync(messageId);
            await botClient.SendMessage(chatId, deleted
                ? $"Scheduled message #{messageId} deleted."
                : $"Scheduled message #{messageId} not found.");
            return TgResult.Ok;
        }

        private async Task<TgResult> ToggleScheduledMessage(long chatId, string[] segments)
        {
            if (segments.Length < 2 || !int.TryParse(segments[1], out var messageId))
            {
                await botClient.SendMessage(chatId, "Usage: toggle_scheduled_message <messageId>");
                return TgResult.Ok;
            }

            var enabled = await registrationService.ToggleScheduledMessageAsync(messageId);
            if (enabled == null)
            {
                await botClient.SendMessage(chatId, $"Scheduled message #{messageId} not found.");
            }
            else
            {
                var status = enabled.Value ? "enabled ✅" : "disabled ⏸";
                await botClient.SendMessage(chatId, $"Scheduled message #{messageId} is now {status}.");
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> ListScheduledMessages(long chatId)
        {
            var items = await registrationService.ListScheduledMessagesAsync();

            if (items.Count == 0)
            {
                await botClient.SendMessage(chatId, "No scheduled messages configured.");
                return TgResult.Ok;
            }

            var sb = new StringBuilder();
            sb.AppendLine("🕐 *Scheduled messages:*");

            foreach (var msg in items)
            {
                var chLabel = msg.Channel != null ? $"`{StringHelper.EscapeMdV2(msg.Channel.Name)}`" : $"Unknown channel";
                var network = msg.Network != null ? $"network `{StringHelper.EscapeMdV2(msg.Network.Name)}`" : $"unknown network";
                var statusIcon = msg.Enabled ? "✅" : "⏸";
                var lastSent = msg.LastSentUtc.HasValue
                    ? StringHelper.EscapeMdV2(timeZoneHelper.ConvertFromUtcToDefaultTimezone(msg.LastSentUtc.Value).ToString("yyyy-MM-dd HH:mm"))
                    : "never";
                sb.AppendLine();
                sb.AppendLine($"{statusIcon} *\\#{msg.Id}* → ch\\#{msg.PublicChannelId} {chLabel} \\({network}\\) every `{msg.IntervalMinutes}` min · last sent: `{lastSent}`");
                if (msg.EnableAt.HasValue)
                    sb.AppendLine($"  enable at: `{StringHelper.EscapeMdV2(timeZoneHelper.ConvertFromUtcToDefaultTimezone(msg.EnableAt.Value).ToString(LocalDateFormat))}`");
                if (msg.DisableAt.HasValue)
                    sb.AppendLine($"  disable at: `{StringHelper.EscapeMdV2(timeZoneHelper.ConvertFromUtcToDefaultTimezone(msg.DisableAt.Value).ToString(LocalDateFormat))}`");
                sb.AppendLine($"  _{StringHelper.EscapeMdV2(msg.Text)}_");
                sb.AppendLine();
            }

            await botClient.SendMessage(chatId, sb.ToString().TrimEnd(), parseMode: ParseMode.MarkdownV2);
            return TgResult.Ok;
        }
    }
}
