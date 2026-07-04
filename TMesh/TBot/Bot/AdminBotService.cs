using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Cms;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TBot.Database;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.Admin;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using static System.Net.Mime.MediaTypeNames;

namespace TBot.Bot
{
    public partial class AdminBotService(
        TelegramBotClient botClient,
        IOptions<TBotOptions> options,
        BotCache botCache,
        RegistrationService registrationService,
        MeshtasticService meshtasticService,
        TimeZoneHelper timeZoneHelper,
        MapMqttService mapMqttService,
        TBotDbContext db)
    {

        private readonly TBotOptions _options = options.Value;

        public async Task<TgResult> HandleAdmin(
            long userId,
            long chatId,
            string text)
        {
            var chatStateWithData = registrationService.GetChatState(userId, chatId);
            var chatState = chatStateWithData?.State;

            bool isSafe = false;
            var prefix = "/admin";
            if (text.StartsWith("/adminsafe"))
            {
                // Allow executing certain non-admin commands with /adminsafe prefix to prevent accidental usage
                prefix = "/adminsafe";
                isSafe = true;
            }

            var noPrefix = text[prefix.Length..].Trim();
            var segments = noPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                if (chatState == ChatState.Admin)
                {
                    // Refresh chat state to prevent expiration while admin is active
                    registrationService.SetChatState(userId, chatId, ChatState.Admin);
                    await botClient.SendMessage(chatId, "Invalid admin command");
                    return TgResult.Ok;
                }
                else if (!isSafe)
                {
                    return TgResult.NotHandled;
                }
                else
                {
                    await botClient.SendMessage(chatId, "Invalid command. To see available admin commands, type /adminsafe help");
                    return TgResult.Ok;
                }
            }
            else if (chatState != ChatState.Admin && segments[0] != "login")
            {
                if (!isSafe)
                {
                    return TgResult.NotHandled;
                }
                else
                {
                    await botClient.SendMessage(chatId, "You must be logged in as admin to use admin commands. To log in, type /adminsafe login");
                }
                return TgResult.Ok;
            }

            if (chatState == ChatState.Admin)
            {
                // Refresh chat state to prevent expiration while admin is active
                registrationService.SetChatState(userId, chatId, ChatState.Admin);
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
                case "refresh":
                    {
                        return await Refresh(userId, chatId, segments);
                    }
                case "public_text_primary":
                    {
                        return await PublicTextPrimary(chatId, noPrefix);
                    }
                case "public_text":
                    {
                        return await PublicText(chatId, noPrefix);
                    }
                case "mqtt_uplink_text":
                    {
                        return await MqttText(chatId, noPrefix);
                    }
                case "chat":
                    {
                        return await Chat(chatId, noPrefix);
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
                        return await UpdatePublicChannel(chatId, noPrefix);
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
                case "change_scheduled_message_channel":
                case "update_scheduled_message":
                    {
                        return await UpdateScheduledMessage(chatId, noPrefix);
                    }
                case "list_scheduled_messages":
                    {
                        return await ListScheduledMessages(chatId);
                    }
                case "add_scheduled_message_variant":
                    {
                        return await AddScheduledMessageVariant(chatId, noPrefix);
                    }
                case "update_scheduled_message_variant":
                    {
                        return await UpdateScheduledMessageVariant(chatId, noPrefix);
                    }
                case "remove_scheduled_message_variant":
                    {
                        return await RemoveScheduledMessageVariant(chatId, segments);
                    }
                case "send_mass_direct_message":
                    {
                        return await SendMassDirectMessage(chatId, noPrefix, segments);
                    }
                case "confirm_mass_direct_message":
                    {
                        return await ConfirmMassDirectMessage(chatId, segments);
                    }
                case "nodeinfo":
                    {
                        return await ShowNodeInfo(chatId, segments);
                    }

                case "queue_status":
                    {
                        return await ShowQueueStatus(chatId, segments);
                    }
                case "help":
                    {
                        var lines = new List<string>
                        {
                            "Available admin commands:",
                            "",
                            "login - Log in to admin mode",
                            "logout - Log out of admin mode",
                            "public_text_primary - Send public text message to primary public channel of the network",
                            "public_text -  Send public text message to public channel",
                            "trace - Start tracing the route to a node",
                            "text - Send direct text to node",
                            "remove_node - Remove node from TMesh",
                            "add_gateway - Add a new gateway",
                            "remove_gateway - Remove a gateway",
                            "list_gateways - List all registered gateways",
                            "list_networks - List all networks",
                            "add_network - Add new network",
                            "update_network - Update network settings",
                            "remove_network - Remove network if there are no registered devices or channels",
                            "add_public_channel - Add public channel to the network",
                            "update_public_channel - Change public channel settings",
                            "remove_public_channel - Remove public channel",
                            "add_scheduled_message - Add a scheduled message to send to public channel",
                            "delete_scheduled_message - Delete a scheduled message",
                            "toggle_scheduled_message - Enable/disable scheduled message",
                            "update_scheduled_message - Update scheduled message text, interval and/or channel",
                            "list_scheduled_messages - Show all scheduled messages",
                            "add_scheduled_message_variant - Adds scheduled message text variant",
                            "update_scheduled_message_variant - Updates scheduled message text variant",
                            "remove_scheduled_message_variant - Removes scheduled message text variant",
                            "send_mass_direct_message - Prepares mass direct message",
                            "confirm_mass_direct_message - Sends prepared mass direct text message",
                            "nodeinfo - find node my id",
                            "queue_status - shows send queues status",
                            "",
                            "Please type command name without arguments to see syntax help."
                        };

                        await botClient.SendLongMessage(chatId, lines);
                        return TgResult.Ok;
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

            var lines = new List<string>
            {
                "🌐 *Networks:*"
            };

            foreach (var network in networks)
            {
                var urlPart = network.Url != null ? $" - {network.Url}" : " No URL";
                lines.Add("");
                lines.Add($"*\\[{network.Id}\\] {StringHelper.EscapeMdV2(network.Name)}* \\(`{StringHelper.EscapeMdV2(network.ShortName)}`\\){StringHelper.EscapeMdV2(urlPart)}");
                lines.Add($"  sort: `{network.SortOrder}` · analytics: `{network.SaveAnalytics}` · disablepongs: `{network.DisablePongs}` · disablewelcome: `{network.DisableWelcomeMessage}`");
                if (network.CommunityUrl != null)
                    lines.Add($"  communityurl: `{StringHelper.EscapeMdV2(network.CommunityUrl)}`");
                if (network.WelcomeUrl != null)
                    lines.Add($"  welcomeurl: `{StringHelper.EscapeMdV2(network.WelcomeUrl)}`");

                lines.Add("");
                var publicChannels = await registrationService.GetPublicChannelsByNetworkAsync(network.Id);
                if (publicChannels.Count == 0)
                {
                    lines.Add("  _No public channels_");
                }
                else
                {
                    foreach (var ch in publicChannels)
                    {
                        var primaryMark = ch.IsPrimary ? " ⭐" : "  ";
                        lines.Add($"{primaryMark} \\#{ch.Id} `{StringHelper.EscapeMdV2(ch.Name)}`");
                        if (ch.SpecialPongText != null)
                        {
                            lines.Add($"    _Special pong text:_ `{StringHelper.EscapeMdV2(ch.SpecialPongText)}`");
                        }
                    }
                }
                lines.Add("");

                var networkGateways = gateways.Values.Where(g => g.NetworkId == network.Id).ToList();
                if (networkGateways.Count > 0)
                {
                    lines.Add("  📡 _Gateways:_");
                    foreach (var gw in networkGateways)
                    {
                        var device = await registrationService.GetDeviceAsync(gw.DeviceId);
                        var hexId = MeshtasticService.GetMeshtasticNodeHexId(gw.DeviceId);
                        var lastSeen = gw.LastSeen.HasValue ? StringHelper.EscapeMdV2(timeZoneHelper.ConvertFromUtcToDefaultTimezone(gw.LastSeen.Value).ToString("yyyy-MM-dd HH:mm")) : "never";
                        lines.Add($"  • {StringHelper.EscapeMdV2(device?.NodeName ?? hexId)} `{StringHelper.EscapeMdV2(hexId)}` \\- seen: {lastSeen}");
                    }
                }
                else
                {
                    lines.Add("  📡 _No gateways_");
                }
            }

            await botClient.SendLongMessage(chatId, lines, parseMode: ParseMode.MarkdownV2);
            return TgResult.Ok;
        }

        private async Task<TgResult> AddNetwork(long chatId, string[] segments)
        {
            // Usage: add_network "<name>" (<shortname> or - for null)  [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [welcomeurl=<value>] [disablewelcomemessage=<true|false>".]

            // Reconstruct the full command from segments to properly parse quoted strings
            var fullCommand = string.Join(' ', segments);

            // Try to extract the name from quotes
            var nameStartIdx = fullCommand.IndexOf('"');
            if (nameStartIdx == -1)
            {
                await botClient.SendMessage(chatId, "Usage: add_network \"<name>\" (<shortname> or - for null) [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [welcomeurl=<value>] [disablewelcomemessage=<true|false>]\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false communityurl=https://community.com welcomeurl=https://welcome.com disablewelcomemessage=false\nNote: Network name must be enclosed in double quotes.");
                return TgResult.Ok;
            }

            var nameEndIdx = fullCommand.IndexOf('"', nameStartIdx + 1);
            if (nameEndIdx == -1)
            {
                await botClient.SendMessage(chatId, "Invalid command: Network name must be enclosed in double quotes.\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false communityurl=https://community.com welcomeurl=https://welcome.com disablewelcomemessage=false");
                return TgResult.Ok;
            }

            var name = fullCommand.Substring(nameStartIdx + 1, nameEndIdx - nameStartIdx - 1);

            // Parse the remaining arguments after the quoted name
            var remainingCommand = fullCommand[(nameEndIdx + 1)..].Trim();
            var remainingSegments = remainingCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (remainingSegments.Length < 1)
            {
                await botClient.SendMessage(chatId, "Usage: add_network \"<name>\" (<shortname> or - for null) [sortorder] [analytics] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [welcomeurl=<value>] [disablewelcomemessage=<true|false>]\nExample: add_network \"Your city name\" CTY 0 true url=https://example.com disablepongs=false communityurl=https://community.com welcomeurl=https://welcome.com disablewelcomemessage=false\nNote: shortname is required.");
                return TgResult.Ok;
            }

            var shortName = remainingSegments[0];
            var sortOrder = remainingSegments.Length >= 2 && int.TryParse(remainingSegments[1], out var so) ? so : 0;
            var saveAnalytics = remainingSegments.Length >= 3 && bool.TryParse(remainingSegments[2], out var sa) && sa;

            string url = null;
            bool disablePongs = false;
            string communityUrl = null;
            string welcomeUrl = null;
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
                    case "welcomeurl":
                        welcomeUrl = value;
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
                Url = url == "-" ? null : url,
                DisablePongs = disablePongs,
                CommunityUrl = communityUrl == "-" ? null : communityUrl,
                WelcomeUrl = welcomeUrl == "-" ? null : welcomeUrl,
                DisableWelcomeMessage = disableWelcomeMessage
            });

            await botClient.SendMessage(chatId, $"Network added: [{network.Id}] {network.Name} (short: {network.ShortName}, analytics: {network.SaveAnalytics}, disablepongs: {network.DisablePongs}, url: {network.Url ?? "-"}, communityurl: {network.CommunityUrl ?? "-"}, welcomeurl: {network.WelcomeUrl ?? "-"}, disablewelcomemessage: {network.DisableWelcomeMessage})");
            return new TgResult
            {
                Handled = true,
                NetworksUpdated = true,
                NetworkWithUpdatedPublicChannels = [network.Id]
            };
        }

        private async Task<TgResult> UpdateNetwork(long chatId, string[] segments)
        {
            // Usage: update_network <id> [name=<value>] [shortname=<value>] [analytics=<true|false>] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [welcomeurl=<value>] [disablewelcomemessage=<true|false>]
            if (segments.Length < 3)
            {
                await botClient.SendMessage(chatId,
                    "Usage: update_network <id> [name=<value>] [shortname=<value>] [analytics=<true|false>] [url=<value>] [disablepongs=<true|false>] [communityurl=<value>] [welcomeurl=<value>] [disablewelcomemessage=<true|false>]\n" +
                    "Example: update_network 1 name=NewName shortname=NN analytics=true url=https://example.com disablepongs=false communityurl=https://community.com welcomeurl=https://welcome.com disablewelcomemessage=false\n" +
                    "To clear url, communityurl, or welcomeurl, use url=-, communityurl=-, or welcomeurl=-\n" +
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
            string newWelcomeUrl = null;
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
                    case "welcomeurl":
                        newWelcomeUrl = value;
                        break;
                    case "disablewelcomemessage":
                        if (bool.TryParse(value, out var dwm))
                            newDisableWelcomeMessage = dwm;
                        break;
                }
            }

            if (newName == null && newShortName == null && newAnalytics == null && newUrl == null && newDisablePongs == null && newCommunityUrl == null && newWelcomeUrl == null && newDisableWelcomeMessage == null)
            {
                await botClient.SendMessage(chatId,
                    "No valid fields provided. Use name=<value>, shortname=<value>, analytics=<true|false>, url=<value>, disablepongs=<true|false>, communityurl=<value>, welcomeurl=<value>, or disablewelcomemessage=<true|false>.");
                return TgResult.Ok;
            }

            await registrationService.UpdateNetworkAsync(networkId, newName, newShortName, newAnalytics, newUrl, newDisablePongs, newCommunityUrl, newWelcomeUrl, newDisableWelcomeMessage);

            var updated = await registrationService.GetNetwork(networkId);
            await botClient.SendMessage(chatId,
                $"Network updated: [{updated.Id}] {updated.Name} (short: {updated.ShortName}, sort: {updated.SortOrder}, analytics: {updated.SaveAnalytics}, disablepongs: {updated.DisablePongs}, url: {updated.Url ?? "-"}, communityurl: {updated.CommunityUrl ?? "-"}, welcomeurl: {updated.WelcomeUrl ?? "-"}, disablewelcomemessage: {updated.DisableWelcomeMessage})");

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
                    NetworkWithUpdatedPublicChannels = [network.Id]
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
                NetworkWithUpdatedPublicChannels = [networkId]
            };
        }

        private async Task<TgResult> UpdatePublicChannel(long chatId, string noPrefix)
        {
            // Usage: update_public_channel <id> <is_primary> <send_node_info_on_secondary> [pong_text="<text>"]
            var segments = noPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 4
                || !int.TryParse(segments[1], out var channelId)
                || !bool.TryParse(segments[2], out var isPrimary)
                || !bool.TryParse(segments[3], out var sendNodeInfoOnSecondary))
            {
                await botClient.SendMessage(chatId,
                    "Usage: update_public_channel <id> <is_primary> <send_node_info_on_secondary> [pong_text=\"<text>\"]\n" +
                    "Example: update_public_channel 2 false true pong_text=\"Pong\"");
                return TgResult.Ok;
            }

            var ch = await registrationService.GetPublicChannelByIdCachedAsync(channelId);
            if (ch == null)
            {
                await botClient.SendMessage(chatId, $"Public channel with ID {channelId} not found.");
                return TgResult.Ok;
            }

            string pongText = null;
            var pongTextMatch = PongTextRegex().Match(noPrefix);
            if (pongTextMatch.Success)
                pongText = pongTextMatch.Groups[1].Value.Replace("\\\"", "\"");

            (var ok, var updated) = await registrationService.TryUpdatePublicChannelAsync(
                channelId,
                isPrimary,
                sendNodeInfoOnSecondary,
                pongText);

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
                NetworkWithUpdatedPublicChannels = [updated.NetworkId]
            };
        }

        private async Task<TgResult> RemovePublicChannel(long chatId, string[] segments)
        {
            if (segments.Length < 2 || !int.TryParse(segments[1], out var channelId))
            {
                await botClient.SendMessage(chatId, "Usage: remove_public_channel <id>\nExample: remove_public_channel 3");
                return TgResult.Ok;
            }

            var ch = await registrationService.GetPublicChannelByIdCachedAsync(channelId);
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
                NetworkWithUpdatedPublicChannels = [ch.NetworkId]
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

            networkId ??= device.NetworkId;

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

            await botClient.SendMessage(chatId, $"Started tracing node `{(device != null ? device.NodeName + $" ({hexId})" : hexId)}` in network [{networkId.Value}] \"{network.Name}\". You will receive updates in this chat as the trace route progresses.");

            return TgResult.Ok;
        }

        private async Task<TgResult> ShowQueueStatus(long chatId, string[] segements)
        {
            // Usage: /admin queue_status <networkId>
            if (segements.Length < 2 || !int.TryParse(segements[1], out var networkId))
            {
                await botClient.SendMessage(chatId, "Usage: /admin queue_status <networkId>\nExample: /admin queue_status 1");
                return TgResult.Ok;
            }

            var highPriorityCount = meshtasticService.GetQueueLength(networkId, MessagePriority.High);
            var normalPriorityCount = meshtasticService.GetQueueLength(networkId, MessagePriority.Normal);
            var lowPriorityCount = meshtasticService.GetQueueLength(networkId, MessagePriority.Low);

            await botClient.SendMessage(chatId, $"Message queue status for network [{networkId}]:\n" +
                $"High priority: {highPriorityCount}\n" +
                $"Normal priority: {normalPriorityCount}\n" +
                $"Low priority: {lowPriorityCount}");

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
            sb.AppendLine($"📟 *{StringHelper.EscapeMdV2(device.NodeName)}* `{StringHelper.EscapeMdV2(hexId)}`");
            sb.AppendLine($"  Registrations: `{registrations.Count}`");
            sb.AppendLine();
            sb.AppendLine($"```");
            sb.AppendLine(StringHelper.EscapeMdV2(json));
            sb.AppendLine($"```");

            await botClient.SendMessage(chatId, sb.ToString().TrimEnd(), parseMode: ParseMode.MarkdownV2);
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

            await registrationService.RegisterGatewayAsync(parsedNodeId, networkId.Value);
            botCache.StoreGatewayRegistraionChat(parsedNodeId, chatId);
            var mqttUsername = hexId;
            var mqttPassword = registrationService.DeriveMqttPasswordForDevice(parsedNodeId);
            var mqttAddress = _options.PublicMqttAddress;
            var mqttTopic = _options.PublicMqttTopic.Replace(TgCommandBotService.NetworkIdToken, MqttService.NetworkSegmentPrefix + networkId.Value.ToString());
            var adminPublicChannels = (await registrationService
                .GetPublicChannelsByNetworkAsync(networkId.Value)
                ).Where(c => c.IsPrimary || !c.SendNodeInfoOnSecondary);
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
                includeInfoAboutFirstSeenMessage: false,
                publicChannelNames: adminPublicChannels.Select(c => c.Name).ToList());


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
            if (!cmd.Contains(' '))
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
            var newMsgId = MeshtasticService.GetNextMeshtasticMessageId();
            botCache.StoreMessageSentByOurNode(newMsgId);

            meshtasticService.SendPublicTextMessage(
                newMsgId,
                announcement,
                relayGatewayId: null,
                hopLimit: int.MaxValue,
                publicChannelName: channelName,
                recipient: channel);

            await botClient.SendMessage(chatId, $"Announcement sent to channel '{channelName}' in network [{networkId}] \"{network.Name}\".");
            return TgResult.Ok;
        }


        private async Task<TgResult> Chat(long chatId, string noPrefix)
        {
            var cmd = noPrefix["chat ".Length..].Trim();
            var publicChannelIdStr = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(publicChannelIdStr))
            {
                await botClient.SendMessage(chatId, "Usage: chat <publicChannelId> [fromDeviceId=\"<fromDeviceId>\"] [fromGatewayId=\"<fromGatewayId>\"]\nPlease specify the public channel ID.");
                return TgResult.Ok;
            }

            if (!int.TryParse(publicChannelIdStr, out var publicChannelId))
            {
                await botClient.SendMessage(chatId, "Invalid public channel ID. Please specify a valid integer public channel ID.");
                return TgResult.Ok;
            }

            var publicChannel = await registrationService.GetPublicChannelByIdCachedAsync(publicChannelId);
            if (publicChannel == null)
            {
                await botClient.SendMessage(chatId, $"Public channel with ID {publicChannelId} not found.");
                return TgResult.Ok;
            }

            var remainingCmd = cmd[(publicChannelIdStr.Length)..].Trim();

            var fromDeviceIdStr = remainingCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(s => s.StartsWith("fromDeviceId=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1]
                .Trim('"');

            long? fromDeviceId = null;

            if (!string.IsNullOrWhiteSpace(fromDeviceIdStr))
            {
                if (!MeshtasticService.TryParseDeviceId(fromDeviceIdStr, out var id))
                {
                    await botClient.SendMessage(chatId, $"Invalid fromDeviceId format: '{fromDeviceIdStr}'. The device ID can be decimal or hex (hex starts with ! or #).");
                    return TgResult.Ok;
                }
                fromDeviceId = id;
            }

            var fromGatewayIdStr = remainingCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(s => s.StartsWith("fromGatewayId=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1]
                .Trim('"');

            long? fromGatewayId = null;
            if (!string.IsNullOrWhiteSpace(fromGatewayIdStr))
            {
                if (!MeshtasticService.TryParseDeviceId(fromGatewayIdStr, out var id))
                {
                    await botClient.SendMessage(chatId, $"Invalid fromGatewayId format: '{fromGatewayIdStr}'. The ID can be decimal or hex (hex starts with ! or #).");
                    return TgResult.Ok;
                }
                fromGatewayId = id;

                var gatewaysLookup = await registrationService.GetGatewaysCached();

                var gateway = gatewaysLookup.GetValueOrDefault(id);

                if (gateway == null)
                {
                    await botClient.SendMessage(chatId, $"Gateway with ID {id} not found. Make sure the gateway is registered before using it in chat.");
                    return TgResult.Ok;
                }

                if (gateway.NetworkId != publicChannel.NetworkId)
                {
                    await botClient.SendMessage(chatId, $"Gateway with ID {id} does not belong to the same network as the public channel. Make sure the gateway is in the correct network before using it in chat.");
                    return TgResult.Ok;
                }
            }

            await botCache.StartChatSession(chatId, new Models.ChatSession.DeviceOrChannelId
            {
                PublicChannelId = publicChannelId,
                ImpersonateDeviceId = fromDeviceId,
                ForceGatewayId = fromGatewayId
            }, db);

            var fromName = $"{_options.MeshtasticNodeNameLong} ({MeshtasticService.GetMeshtasticNodeHexId(_options.MeshtasticNodeId)})";
            if (fromDeviceId.HasValue)
            {
                var device = await registrationService.GetDeviceAsync(fromDeviceId.Value);

                if (device != null)
                {
                    fromName = $"{device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(device.DeviceId)})";
                }
                else
                {
                    fromName = $"Unknown node - {MeshtasticService.GetMeshtasticNodeHexId(fromDeviceId.Value)}";
                }
            }

            var gatewayName = "All gateways";
            if (fromGatewayId.HasValue)
            {
                var gatewayDevice = await registrationService.GetDeviceAsync(fromGatewayId.Value);
                if (gatewayDevice != null)
                {
                    gatewayName = $"{gatewayDevice.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(gatewayDevice.DeviceId)})";
                }
                else
                {
                    gatewayName = $"Unknown gateway - {MeshtasticService.GetMeshtasticNodeHexId(fromGatewayId.Value)}";
                }
            }

            await botClient.SendMessage(chatId, $"You are now chatting in channel '{publicChannel.Name}' in network [{publicChannel.NetworkId}]. From device - {fromName}. Gateway - {gatewayName}");
            return TgResult.Ok;
        }

        private async Task<TgResult> MqttText(long chatId, string noPrefix)
        {
            var cmd = noPrefix["mqtt_uplink_text".Length..].Trim();
            var publicChannelIdStr = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

            if (!cmd.Contains(' '))
            {
                await botClient.SendMessage(chatId, "Usage: mqtt_uplink_text <publicChannelId> <fromNodeId> <text>\nPlease specify the public channel ID and announcement text.");
                return TgResult.Ok;
            }

            if (!int.TryParse(publicChannelIdStr, out var publicChannelId))
            {
                await botClient.SendMessage(chatId, "Invalid public channel ID. Please specify a valid integer public channel ID.");
                return TgResult.Ok;
            }

            var publicChannel = await registrationService.GetPublicChannelByIdCachedAsync(publicChannelId);
            if (publicChannel == null)
            {
                await botClient.SendMessage(chatId, $"Public channel with ID {publicChannelId} not found.");
                return TgResult.Ok;
            }

            var remainingCmd = cmd[(publicChannelIdStr.Length + 1)..].Trim();

            var fromNodeIdStr = remainingCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fromNodeIdStr))
            {
                await botClient.SendMessage(chatId, "Please specify the fromNodeId and announcement text.");
                return TgResult.Ok;
            }
            if (!MeshtasticService.TryParseDeviceId(fromNodeIdStr, out var fromNodeId))
            {
                await botClient.SendMessage(chatId, $"Invalid fromNodeId format: '{fromNodeIdStr}'. The node ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }

            var announcement = remainingCmd[(fromNodeIdStr.Length + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(announcement))
            {
                await botClient.SendMessage(chatId, "Announcement text cannot be empty.");
                return TgResult.Ok;
            }

            var newMsgId = MeshtasticService.GetNextMeshtasticMessageId();
            botCache.StoreMessageSentByOurNode(newMsgId);

            var envelope = meshtasticService.PackPublicTextMessage(
                newMsgId,
                announcement,
                replyToMessageId: null,
                hopLimit: _options.OutgoingMessageHopLimit,
                recipient: publicChannel,
                channelName: publicChannel.Name,
                fromNodeId: fromNodeId);

            envelope.Packet.RxSnr = 1;
            envelope.Packet.RxRssi = -30;
            envelope.Packet.HopLimit = envelope.Packet.HopStart - 1;
            envelope.Packet.TransportMechanism = Meshtastic.Protobufs.MeshPacket.Types.TransportMechanism.TransportLora;
            envelope.Packet.RelayNode = 35;
            envelope.Packet.RxTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            bool sent = await mapMqttService.PublishMeshtasticMessage(publicChannel.NetworkId, MeshtasticService.DefaultOkToMqtt, envelope);

            if (sent)
            {
                await botClient.SendMessage(chatId, $"Announcement for channel '{publicChannel.Name}' in network [{publicChannel.NetworkId}] is sent to MQTT uplink.\n\nMessage ID:\n{newMsgId}, ChannelXor: {envelope.Packet.Channel}");
            }
            else
            {
                await botClient.SendMessage(chatId, $"Failed to send announcement to MQTT uplink for channel '{publicChannel.Name}' in network [{publicChannel.NetworkId}]. Please try again later.");
            }
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
            var newMsgId = MeshtasticService.GetNextMeshtasticMessageId();
            botCache.StoreMessageSentByOurNode(newMsgId);

            meshtasticService.SendPublicTextMessage(
                newMsgId,
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

        private async Task<TgResult> Refresh(long userId, long chatId, string[] segments)
        {
            // /admin refresh <networkId>
            if (segments == null || segments.Length <= 1)
            {
                await botClient.SendMessage(chatId, "Usage: /admin refresh <networkId>\nExample: /admin refresh 1");
                return TgResult.Ok;
            }

            if (!int.TryParse(segments[1], out var networkId))
            {
                await botClient.SendMessage(chatId, "Invalid network ID. Please specify a valid integer network ID.\nExample: /admin refresh 1");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId} not found.");
                return TgResult.Ok;
            }

            var names = new StringBuilder();
            var channels = await registrationService.GetAllNodeInfoChannelsCached();
            foreach (var channel in channels.Where(c => c.NetworkId == networkId))
            {
                meshtasticService.SendVirtualNodeInfo(channel.Name, channel, int.MaxValue);
                names.AppendLine($"  • {channel.Name} (ID: {channel.Id})");
            }
            await botClient.SendMessage(chatId, $"Sent virtual node info to the following channels in network [{networkId}] \"{network.Name}\":\n{names}");
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

        private static bool TryParseLocalDate(string value, out DateTime result)
            => DateTime.TryParseExact(value, LocalDateFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result);


        public static bool IsValidRegexPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return false;

            try
            {
                _ = new Regex(pattern);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private async Task<TgResult> ConfirmMassDirectMessage(long chatId, string[] segments)
        {
            // Usage: confirm_mass_direct_message <code>

            if (segments.Length < 2)
            {
                await botClient.SendMessage(chatId, "Usage: confirm_mass_direct_message <code>");
                return TgResult.Ok;
            }

            var code = segments[1];
            var msg = botCache.GetMassDirectMessage(code);
            if (msg == null)
            {
                await botClient.SendMessage(chatId, $"No mass direct message found for code: {code}");
                return TgResult.Ok;
            }

            DateTime? activeAfterUtc = msg.MaxNodeAgeHours.HasValue ? DateTime.UtcNow.AddHours(-msg.MaxNodeAgeHours.Value) : (DateTime?)null;

            var deviceKeys = await registrationService.GetDeviceKeysForMassDirectMessage(
                msg.NetworkId,
                activeAfterUtc,
                msg.NodeNameRegexPattern,
                maxRecords: null);

            foreach (var deviceKey in deviceKeys)
            {
                meshtasticService.SendDirectTextMessage(
                     deviceKey.DeviceId,
                     deviceKey.NetworkId,
                     deviceKey.PublicKey,
                        msg.Text,
                        replyToMessageId: null,
                        relayGatewayId: botCache.GetDeviceGateway(deviceKey.DeviceId)?.GatewayId,
                        hopLimit: int.MaxValue,
                        priority: MessagePriority.Low
                    );
            }

            await botClient.SendMessage(chatId,
                $"Mass direct message queued to {deviceKeys.Count} devices in network [{msg.NetworkId}].\n" +
                $"Check status with /admin queue_status\n" +
                $"Message text: {msg.Text}\n" +
                (msg.MaxNodeAgeHours.HasValue ? $"- Max node age: {msg.MaxNodeAgeHours.Value} hours\n" : "") +
                (!string.IsNullOrEmpty(msg.NodeNameRegexPattern) ? $"- Node name filter regex: {msg.NodeNameRegexPattern}\n" : ""));

            return TgResult.Ok;
        }

        private async Task<TgResult> SendMassDirectMessage(long chatId, string noPrefix, string[] segments)
        {
            // Usage: send_mass_direct_message network=<network_id> text="<message text>" [max_node_age_hours=<hours>] [node_name_filter_regex="regex"]
            if (segments.Length < 3 || !noPrefix.Contains("text=\"") || !noPrefix.Contains("network_id="))
            {
                await botClient.SendMessage(chatId,
                    "Usage: send_mass_direct_message network_id=<network_id> text=\"<message text>\" [max_node_age_hours=<hours>] [node_name_filter_regex=\"regex\"]\n" +
                    "Example: send_mass_direct_message network_id=1 text=\"Hello mesh!\" max_node_age_hours=24 node_name_filter_regex=\"^Node.*\"\n" +
                    "Use \\\" to include double quotes in the message text or regex.\n" +
                    "max_node_age_hours filters out devices that have not been seen in the last specified hours. node_name_filter_regex filters devices by their name using a regular expression.");

                return TgResult.Ok;
            }

            var workingString = noPrefix;

            var textMatch = TextRegex().Match(workingString);

            if (!textMatch.Success)
            {
                await botClient.SendMessage(chatId,
                    "Could not parse text parameter. Use: text=\"your message here\"");
                return TgResult.Ok;
            }
            var text = textMatch.Groups[1].Value.Replace("\\\"", "\"");

            //remove string text="..." from
            workingString = noPrefix.Remove(textMatch.Index, textMatch.Length).Trim();


            string nodeNameRegexPattern = null;

            var regexMatch = NodeNameFilterRegex().Match(workingString);

            if (regexMatch.Success)
            {
                nodeNameRegexPattern = regexMatch.Groups[1].Value.Replace("\\\"", "\"");
                if (!IsValidRegexPattern(nodeNameRegexPattern))
                {
                    await botClient.SendMessage(chatId,
                        $"Invalid regular expression pattern for node_name_filter_regex: '{nodeNameRegexPattern}'. Please provide a valid regex pattern.");
                    return TgResult.Ok;
                }
                workingString = workingString.Remove(regexMatch.Index, regexMatch.Length).Trim();
            }


            var maxAgeHoursMatch = MaxNodeAgeRegex().Match(workingString);

            int? maxAgeHours = null;
            if (maxAgeHoursMatch.Success)
            {
                if (int.TryParse(maxAgeHoursMatch.Groups[1].Value, out var hours) && hours > 0)
                {
                    maxAgeHours = int.Parse(maxAgeHoursMatch.Groups[1].Value);
                }
                else
                {
                    await botClient.SendMessage(chatId,
                        $"Invalid value for max_node_age_hours: '{maxAgeHoursMatch.Groups[1].Value}'. It must be a positive integer.");
                    return TgResult.Ok;
                }
                workingString = workingString.Remove(maxAgeHoursMatch.Index, maxAgeHoursMatch.Length).Trim();
            }

            var networkIdMatch = NetworkIdRegex().Match(workingString);

            if (!networkIdMatch.Success || !int.TryParse(networkIdMatch.Groups[1].Value, out var networkId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid or missing network ID. Please specify a valid integer network ID using network_id=<network_id>.");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId, $"Network with ID {networkId} not found.");
                return TgResult.Ok;
            }

            var msg = new MassDirectMessage
            {
                MaxNodeAgeHours = maxAgeHours,
                Text = text,
                NetworkId = networkId,
                NodeNameRegexPattern = nodeNameRegexPattern
            };

            var code = RegistrationService.GenerateRandomCode();
            botCache.StoreMassDirectMessage(code, msg);

            DateTime? activeAfterUtc = maxAgeHours.HasValue ? DateTime.UtcNow.AddHours(-maxAgeHours.Value) : (DateTime?)null;

            (var deviceCount, var sampleNames) = await registrationService.GetDeviceCountForMassDirectMessage(networkId, activeAfterUtc, nodeNameRegexPattern, sampleSize: 10);


            var sampleNamesSb = new StringBuilder();
            foreach (var name in sampleNames)
            {
                if (sampleNamesSb.Length > 0)
                {
                    sampleNamesSb.Append(", ");
                }
                sampleNamesSb.Append(name);
            }

            await botClient.SendMessage(chatId,
                $"Mass direct message setup:\n" +
                $"- Network: [{networkId}] \"{network.Name}\"\n" +
                $"- Text: {text}\n" +
                (maxAgeHours.HasValue ? $"- Max node age: {maxAgeHours.Value} hours\n" : "") +
                (!string.IsNullOrEmpty(nodeNameRegexPattern) ? $"- Node name filter regex: {nodeNameRegexPattern}\n" : "") +
                $"Devices matching criteria: {deviceCount}\n" +
                $"Sample names: {sampleNamesSb}\n" +
                $"To confirm sending the message to all matching devices, please execute command /admin confirm_mass_direct_message {code}");

            return TgResult.Ok;
        }

        private async Task<TgResult> AddScheduledMessage(long chatId, string noPrefix, string[] segments)
        {
            // Usage: add_scheduled_message <publicChannelId> <intervalMinutes> text="<message text>" [enable_at=yyyy-MM-ddTHH:mm:ss] [disable_at=yyyy-MM-ddTHH:mm:ss]
            if (segments.Length < 3 || !noPrefix.Contains("text=\""))
            {
                await botClient.SendMessage(chatId,
                    "Usage: add_scheduled_message <publicChannelId> <intervalMinutes> text=\"<message text>\" [enable_at=yyyy-MM-ddTHH:mm:ss] [disable_at=yyyy-MM-ddTHH:mm:ss]\n" +
                    "Example: add_scheduled_message 1 60 text=\"Hello mesh!\" enable_at=2025-06-01T08:00:00 disable_at=2025-09-01T00:00:00\n" +
                    "Use \\\" to include double quotes in the message text.\n" +
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

            var channel = await registrationService.GetPublicChannelByIdCachedAsync(publicChannelId);
            if (channel == null)
            {
                await botClient.SendMessage(chatId, $"Public channel with ID {publicChannelId} not found.");
                return TgResult.Ok;
            }

            // Everything after "add_scheduled_message <id> <interval> "
            var afterFixed = noPrefix[$"add_scheduled_message {segments[1]} {segments[2]} ".Length..].Trim();

            // Extract text="..." — supports \" escapes inside the quoted value
            var textMatch = TextRegex().Match(afterFixed);
            if (!textMatch.Success)
            {
                await botClient.SendMessage(chatId,
                    "Could not parse text parameter. Use: text=\"your message here\"");
                return TgResult.Ok;
            }
            var text = textMatch.Groups[1].Value.Replace("\\\"", "\"");

            // Remove the matched text="..." from afterFixed to parse remaining key=value tokens
            var remainder = afterFixed.Remove(textMatch.Index, textMatch.Length).Trim();

            // Parse enable_at / disable_at from remainder
            DateTime? enableAtUtc = null;
            DateTime? disableAtUtc = null;
            foreach (var token in remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = token.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = token[..eqIdx].ToLowerInvariant();
                var val = token[(eqIdx + 1)..];
                if (key != "enable_at" && key != "disable_at") continue;
                if (!TryParseLocalDate(val, out var localDt))
                {
                    await botClient.SendMessage(chatId,
                        $"Invalid date format for {key}: '{val}'. Required format: {LocalDateFormat}");
                    return TgResult.Ok;
                }
                var utcDt = timeZoneHelper.ConvertFromDefaultTimezoneToUtc(localDt);
                if (key == "enable_at") enableAtUtc = utcDt;
                else disableAtUtc = utcDt;
            }

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

        private async Task<TgResult> UpdateScheduledMessage(long chatId, string noPrefix)
        {
            // Usage: update_scheduled_message <message_id> [text="<text>"] [interval="<minutes>"] [channel="<channel_id>"]
            var cmd = noPrefix["update_scheduled_message".Length..].Trim();
            var firstSpace = cmd.IndexOf(' ');
            if (firstSpace < 0)
            {
                await botClient.SendMessage(chatId,
                    "Usage: update_scheduled_message <message_id> [text=\"<text>\"] [interval=\"<minutes>\"] [channel=\"<channel_id>\"]\n" +
                    "At least one of text, interval, or channel must be provided.\n" +
                    "Example: update_scheduled_message 3 text=\"New text\" interval=\"60\"");
                return TgResult.Ok;
            }

            var idStr = cmd[..firstSpace];
            if (!int.TryParse(idStr, out var messageId))
            {
                await botClient.SendMessage(chatId, "Invalid scheduled message ID.");
                return TgResult.Ok;
            }

            var argsStr = cmd[(firstSpace + 1)..].Trim();

            string newText = null;
            int? newInterval = null;
            int? newChannelId = null;

            var textMatch = TextRegex().Match(argsStr);
            if (textMatch.Success)
                newText = textMatch.Groups[1].Value.Replace("\\\"", "\"");

            var intervalMatch = IntervalRegex().Match(argsStr);
            if (intervalMatch.Success && int.TryParse(intervalMatch.Groups[1].Value, out var parsedInterval))
                newInterval = parsedInterval;

            var channelMatch = ChannelRegex().Match(argsStr);
            if (channelMatch.Success && int.TryParse(channelMatch.Groups[1].Value, out var parsedChannelId))
                newChannelId = parsedChannelId;

            if (newText == null && newInterval == null && newChannelId == null)
            {
                await botClient.SendMessage(chatId,
                    "Nothing to update. Provide at least one of: text=\"...\", interval=\"...\", channel=\"...\"");
                return TgResult.Ok;
            }

            var msg = await registrationService.GetScheduledMessageByIdAsync(messageId);
            if (msg == null)
            {
                await botClient.SendMessage(chatId, $"Scheduled message #{messageId} not found.");
                return TgResult.Ok;
            }

            if (newText != null && string.IsNullOrWhiteSpace(newText))
            {
                await botClient.SendMessage(chatId, "Text cannot be empty.");
                return TgResult.Ok;
            }

            if (newText != null && !MeshtasticService.CanSendMessage(newText))
            {
                await botClient.SendMessage(chatId,
                    $"Text is too long. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes.");
                return TgResult.Ok;
            }

            if (newInterval is <= 0)
            {
                await botClient.SendMessage(chatId, "Interval must be a positive number of minutes.");
                return TgResult.Ok;
            }

            if (newChannelId.HasValue)
            {
                var channel = await registrationService.GetPublicChannelByIdCachedAsync(newChannelId.Value);
                if (channel == null)
                {
                    await botClient.SendMessage(chatId, $"Public channel #{newChannelId.Value} not found.");
                    return TgResult.Ok;
                }
            }

            await registrationService.UpdateScheduledMessageAsync(messageId, newText, newInterval, newChannelId);

            var parts = new List<string>();
            if (newText != null) parts.Add($"text → _{StringHelper.EscapeMd(newText)}_");
            if (newInterval.HasValue) parts.Add($"interval → *{newInterval}* min");
            if (newChannelId.HasValue) parts.Add($"channel → *#{newChannelId}*");

            await botClient.SendMessage(chatId,
                $"Scheduled message *#{messageId}* updated: {string.Join(", ", parts)}.",
                parseMode: ParseMode.Markdown);
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

            // Build the full list as individual lines so we can split at any line boundary.
            var lines = new List<string>
            {
                "🕐 *Scheduled messages:*"
            };

            foreach (var msg in items)
            {
                var chLabel = msg.Channel != null ? $"`{StringHelper.EscapeMdV2(msg.Channel.Name)}`" : $"Unknown channel";
                var network = msg.Network != null ? $"network `{StringHelper.EscapeMdV2(msg.Network.Name)}`" : $"unknown network";
                var statusIcon = msg.Enabled ? "✅" : "⏸";
                var lastSent = msg.LastSentUtc.HasValue
                    ? StringHelper.EscapeMdV2(timeZoneHelper.ConvertFromUtcToDefaultTimezone(msg.LastSentUtc.Value).ToString("yyyy-MM-dd HH:mm"))
                    : "never";

                lines.Add("");
                lines.Add($"{statusIcon} *\\#{msg.Id}* → ch\\#{msg.PublicChannelId} {chLabel} \\({network}\\) every `{msg.IntervalMinutes}` min · last sent: `{lastSent}`");
                if (msg.EnableAt.HasValue)
                    lines.Add($"  enable at: `{StringHelper.EscapeMdV2(timeZoneHelper.ConvertFromUtcToDefaultTimezone(msg.EnableAt.Value).ToString(LocalDateFormat))}`");
                if (msg.DisableAt.HasValue)
                    lines.Add($"  disable at: `{StringHelper.EscapeMdV2(timeZoneHelper.ConvertFromUtcToDefaultTimezone(msg.DisableAt.Value).ToString(LocalDateFormat))}`");
                lines.Add($"  `[0]` _{StringHelper.EscapeMdV2(msg.Text)}_" + (msg.LastSentVariantIndex == 0 ? " ◀" : ""));
                for (int i = 0; i < msg.Variants.Count; i++)
                {
                    var v = msg.Variants[i];
                    var current = msg.LastSentVariantIndex == i + 1 ? " ◀" : "";
                    lines.Add($"  `[{i + 1}]` id\\={v.Id} _{StringHelper.EscapeMdV2(v.Text)}_{current}");
                }
            }

            await botClient.SendLongMessage(chatId, lines, ParseMode.MarkdownV2);
            return TgResult.Ok;
        }

        private async Task<TgResult> AddScheduledMessageVariant(long chatId, string noPrefix)
        {
            // Usage: add_scheduled_message_variant <scheduled_message_id> text="<text>"
            var cmd = noPrefix["add_scheduled_message_variant".Length..].Trim();
            var firstSpace = cmd.IndexOf(' ');
            if (firstSpace < 0 || !cmd.Contains("text=\""))
            {
                await botClient.SendMessage(chatId,
                    "Usage: add_scheduled_message_variant <scheduled_message_id> text=\"<message text>\"\n" +
                    "Example: add_scheduled_message_variant 3 text=\"Variant text here\"");
                return TgResult.Ok;
            }

            var idStr = cmd[..firstSpace];
            if (!int.TryParse(idStr, out var scheduledMessageId))
            {
                await botClient.SendMessage(chatId, "Invalid scheduled message ID.");
                return TgResult.Ok;
            }

            var scheduled = await registrationService.GetScheduledMessageByIdAsync(scheduledMessageId);
            if (scheduled == null)
            {
                await botClient.SendMessage(chatId, $"Scheduled message #{scheduledMessageId} not found.");
                return TgResult.Ok;
            }

            var afterId = cmd[(firstSpace + 1)..].Trim();
            var textMatch = TextRegex().Match(afterId);
            if (!textMatch.Success)
            {
                await botClient.SendMessage(chatId, "Could not parse text parameter. Use: text=\"your variant text here\"");
                return TgResult.Ok;
            }
            var text = textMatch.Groups[1].Value.Replace("\\\"", "\"");

            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, "Variant text cannot be empty.");
                return TgResult.Ok;
            }

            if (!MeshtasticService.CanSendMessage(text))
            {
                await botClient.SendMessage(chatId,
                    $"Variant text is too long. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes.");
                return TgResult.Ok;
            }

            var variant = await registrationService.AddScheduledMessageVariantAsync(scheduledMessageId, text);
            await botClient.SendMessage(chatId,
                $"Variant #{variant.Id} added to scheduled message #{scheduledMessageId}:\n{StringHelper.EscapeMd(text)}",
                parseMode: ParseMode.Markdown);
            return TgResult.Ok;
        }

        private async Task<TgResult> UpdateScheduledMessageVariant(long chatId, string noPrefix)
        {
            // Usage: update_scheduled_message_variant <variant_id> text="<text>"
            var cmd = noPrefix["update_scheduled_message_variant".Length..].Trim();
            var firstSpace = cmd.IndexOf(' ');
            if (firstSpace < 0 || !cmd.Contains("text=\""))
            {
                await botClient.SendMessage(chatId,
                    "Usage: update_scheduled_message_variant <variant_id> text=\"<new text>\"\n" +
                    "Example: update_scheduled_message_variant 5 text=\"Updated variant text\"");
                return TgResult.Ok;
            }

            var idStr = cmd[..firstSpace];
            if (!int.TryParse(idStr, out var variantId))
            {
                await botClient.SendMessage(chatId, "Invalid variant ID.");
                return TgResult.Ok;
            }

            var afterId = cmd[(firstSpace + 1)..].Trim();
            var textMatch = TextRegex().Match(afterId);
            if (!textMatch.Success)
            {
                await botClient.SendMessage(chatId, "Could not parse text parameter. Use: text=\"your new text here\"");
                return TgResult.Ok;
            }
            var text = textMatch.Groups[1].Value.Replace("\\\"", "\"");

            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, "Variant text cannot be empty.");
                return TgResult.Ok;
            }

            if (!MeshtasticService.CanSendMessage(text))
            {
                await botClient.SendMessage(chatId,
                    $"Variant text is too long. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes.");
                return TgResult.Ok;
            }

            var updated = await registrationService.UpdateScheduledMessageVariantAsync(variantId, text);
            if (!updated)
            {
                await botClient.SendMessage(chatId, $"Scheduled message variant #{variantId} not found.");
                return TgResult.Ok;
            }

            await botClient.SendMessage(chatId,
                $"Variant *#{variantId}* updated:\n_{StringHelper.EscapeMd(text)}_",
                parseMode: ParseMode.Markdown);
            return TgResult.Ok;
        }

        private async Task<TgResult> RemoveScheduledMessageVariant(long chatId, string[] segments)
        {
            if (segments.Length < 2 || !int.TryParse(segments[1], out var variantId))
            {
                await botClient.SendMessage(chatId, "Usage: remove_scheduled_message_variant <variant_id>");
                return TgResult.Ok;
            }

            var deleted = await registrationService.DeleteScheduledMessageVariantAsync(variantId);
            await botClient.SendMessage(chatId, deleted
                ? $"Scheduled message variant #{variantId} deleted."
                : $"Scheduled message variant #{variantId} not found.");
            return TgResult.Ok;
        }

        [GeneratedRegex(@"pong_text=""((?:[^""\\]|\\.)*)""")]
        private static partial Regex PongTextRegex();
        [GeneratedRegex(@"text=""((?:[^""\\]|\\.)*)""")]
        private static partial Regex TextRegex();
        [GeneratedRegex(@"node_name_filter_regex=""((?:[^""\\]|\\.)*)""")]
        private static partial Regex NodeNameFilterRegex();
        [GeneratedRegex(@"max_node_age_hours=(\d+)")]
        private static partial Regex MaxNodeAgeRegex();
        [GeneratedRegex(@"network_id=(\d+)")]
        private static partial Regex NetworkIdRegex();
        [GeneratedRegex(@"interval=""(\d+)""")]
        private static partial Regex IntervalRegex();
        [GeneratedRegex(@"channel=""(\d+)""")]
        private static partial Regex ChannelRegex();
    }
}
