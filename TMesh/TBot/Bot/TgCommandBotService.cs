using Linux.Bluetooth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using TBot.Database;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.ChatSession;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot.Bot
{
    public class TgCommandBotService(
        TelegramBotClient botClient,
        IOptions<TBotOptions> options,
        RegistrationService registrationService,
        MeshtasticService meshtasticService,
        BotCache botCache,
        ILogger<TgCommandBotService> logger,
        IServiceProvider services,
        TBotDbContext db)
    {
        private readonly TBotOptions _options = options.Value;
        public const string NetworkIdToken = "{{NetworkId}}";

        public async Task<TgResult> HandleCommand(
         long userId,
         long chatId,
         Message message)
        {
            if (message.Text?.StartsWith("/start", StringComparison.OrdinalIgnoreCase) == true
                && (message.Text.Length == 6 || message.Text[6] == ' ' || message.Text[6] == '@'))
            {
                return await HandleStart(userId, chatId, message);
            }
            if (message.Text?.StartsWith("/kill", StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandleKill(userId, chatId, message);
            }
            if (message.Text?.StartsWith("/disable", StringComparison.OrdinalIgnoreCase) == true
                && (message.Text.Length == 8 || message.Text[8] == ' ' || message.Text[8] == '@'))
            {
                return await HandleDisable(chatId);
            }
            if (message.Text?.StartsWith("/end_chat", StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandleStopChat(chatId);
            }
            if (message.Text?.StartsWith("/chat", StringComparison.OrdinalIgnoreCase) == true
                && (message.Text.Length == 5 || message.Text[5] == ' ' || message.Text[5] == '@'))
            {
                var chatArg = ExtractSingleArgFromCommand(message.Text, "/chat");
                return await HandleChatDeviceCommand(userId, chatId, chatArg, message.From.GetUserNameOrName());
            }
            if (message.Text?.StartsWith("/chat_channel", StringComparison.OrdinalIgnoreCase) == true
               && (message.Text.Length == 13 || message.Text[13] == ' ' || message.Text[13] == '@'))
            {
                var chatArg = ExtractSingleArgFromCommand(message.Text, "/chat_channel");
                return await HandleChatChannelCommand(userId, chatId, chatArg, message.From?.Username);
            }
            if (message.Text?.StartsWith("/add_device", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractSingleArgFromCommand(message.Text, "/add_device");
                return await StartAddDevice(userId, chatId, deviceIdFromCommand);
            }
            if (message.Text?.StartsWith("/add_channel", StringComparison.OrdinalIgnoreCase) == true)
            {
                var (networkId, name, key, mode) = ExtractChannelFromCommand(message.Text, "/add_channel");
                return await StartAddChannel(userId, chatId, networkId, name, key, mode);
            }
            if (message.Text?.StartsWith("/remove_device", StringComparison.OrdinalIgnoreCase) == true)
            {
                bool isRemoveFromAll = message.Text.StartsWith("/remove_device_from_all_chats", StringComparison.OrdinalIgnoreCase);
                var deviceIdFromCommand = ExtractSingleArgFromCommand(message.Text, isRemoveFromAll ? "/remove_device_from_all_chats" : "/remove_device");
                return await StartRemoveDevice(userId, chatId, deviceIdFromCommand, isRemoveFromAll);
            }
            if (message.Text?.StartsWith("/remove_channel", StringComparison.OrdinalIgnoreCase) == true)
            {
                bool isRemoveFromAll = message.Text.StartsWith("/remove_channel_from_all_chats", StringComparison.OrdinalIgnoreCase);
                var channelIdFromCommand = ExtractSingleArgFromCommand(message.Text, isRemoveFromAll ? "/remove_channel_from_all_chats" : "/remove_channel");
                return await StartRemoveChannel(userId, chatId, userId, channelIdFromCommand, isRemoveFromAll);
            }
            if (message.Text?.StartsWith("/status", StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandleStatus(chatId, message.Text);
            }
            if (message.Text?.StartsWith("/position", StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandlePosition(chatId, message.Text);
            }
            if (message.Text?.StartsWith("/promote_to_gateway", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractSingleArgFromCommand(message.Text, "/promote_to_gateway");
                return await StartPromoteToGateway(userId, chatId, deviceIdFromCommand);
            }
            if (message.Text?.StartsWith("/demote_from_gateway", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractSingleArgFromCommand(message.Text, "/demote_from_gateway");
                return await StartDemoteFromGateway(userId, chatId, deviceIdFromCommand);
            }
            if (message.Text?.StartsWith("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "There is no active operation to stop it.");
                return TgResult.Ok;
            }
            if (message.Text?.StartsWith("/list_networks", StringComparison.OrdinalIgnoreCase) == true)
            {
                return await ListNetworks(chatId);
            }
            if (message.Text?.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) == true)
            {
                var handled = await HandleAdmin(
                    userId,
                    chatId,
                    message.Text);

                if (handled.Handled)
                {
                    return handled;
                }
            }

            return TgResult.NotHandled;
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
            sb.AppendLine("🌐 *Available networks:*");

            foreach (var network in networks)
            {
                sb.AppendLine();
                var urlPart = !string.IsNullOrEmpty(network.Url) ? $" - [{StringHelper.EscapeMd(network.Url)}]({network.Url})" : string.Empty;
                sb.AppendLine($"📍 *{StringHelper.EscapeMd(network.Name)}* - ID `{network.Id}` {urlPart}");

                var publicChannels = await registrationService.GetPublicChannelsByNetworkAsync(network.Id);
                if (publicChannels.Count == 0)
                {
                    sb.AppendLine("  _No public channels_");
                }
                else
                {
                    foreach (var ch in publicChannels.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Name))
                    {
                        var primaryMark = ch.IsPrimary ? " ⭐" : "  ";
                        sb.AppendLine($"{primaryMark} *{StringHelper.EscapeMd(ch.Name)}* - key: {StringHelper.EscapeMd(MeshtasticService.PskKeyToBase64(ch.Key))}");
                    }
                }

                var networkGateways = gateways.Values.Where(g => g.NetworkId == network.Id).ToList();
                if (networkGateways.Count > 0)
                {
                    sb.AppendLine($"  📡 _Gateways:_");
                    foreach (var gw in networkGateways)
                    {
                        var device = await registrationService.GetDeviceAsync(gw.DeviceId);
                        var hexId = MeshtasticService.GetMeshtasticNodeHexId(gw.DeviceId);
                        var name = device?.NodeName ?? hexId;
                        sb.AppendLine($"  • {StringHelper.EscapeMd(name)} `{hexId}`");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine($"_If your city is not listed and you are ready to convert your device to a TMesh gateway, please contact the administrator - {StringHelper.EscapeMd(_options.AdminTgContact)}_");

            await botClient.SendMessage(chatId, sb.ToString().TrimEnd(), parseMode: ParseMode.Markdown);
            return TgResult.Ok;
        }

        private async Task<TgResult> HandleStatus(long chatId, string cmdText)
        {
            var channelRegs = await registrationService.GetChannelNamesByChatId(chatId);
            var channelApprovals = await registrationService.GetChannelApprovalsByChatId(chatId);
            var devices = await registrationService.GetDeviceNamesByChatId(chatId);
            var deviceApprovals = await registrationService.GetDeviceApprovalsByChatId(chatId);
            var networks = await registrationService.GetNetworksLookupCached();
            var chatSession = botCache.GetActiveChatSession(chatId);
            var registeredChat = await registrationService.GetTgChatByChatIdAsync(chatId);
            var response = new StringBuilder();
            if (registeredChat != null)
            {
                if (registeredChat.IsActive)
                {
                    response.AppendLine("This chat is *active* in TMesh and can get new chat requests from Meshtastic devices and channels\\.");
                    response.AppendLine($"Meshtastic command: `/chat {StringHelper.EscapeMdV2(registeredChat.ChatName)}` via DM to {StringHelper.EscapeMd(_options.MeshtasticNodeNameLong)} or private channel registered with TMesh\\.");
                }
                else
                {
                    response.AppendLine($"This chat is registered in TMesh with name {StringHelper.EscapeMdV2(registeredChat.ChatName)} but currently *disabled* for new chat requests\\.");
                    response.AppendLine($"Approved or registered devices and channels still can start new chat sessions.");
                    response.AppendLine($"To allow incomming chat request from new Meshtastic devices run command: `/start`");
                }
            }
            else
            {
                response.AppendLine("This chat is not registered in TMesh and cannot receive chat requests from Meshtastic devices or channels\\.");
                response.AppendLine($"To register this chat in TMesh run command: `/start`");
            }
            response.AppendLine();

            if (devices.Count == 0
                && channelRegs.Count == 0
                && channelApprovals.Count == 0
                && deviceApprovals.Count == 0
                && chatSession == null)
            {
                response.AppendLine("No registered devices or channels\\. You can register a new device with the /add\\_device command, channel with /add\\_channel command, or start a temporary chat with `/chat \\!\\<deviceId\\>`\\. Please remove the bot from the group if you don't need it\\.");
                await botClient.SendMessage(chatId, response.ToString().TrimEnd(), parseMode: ParseMode.MarkdownV2);
            }
            else
            {
                var filter = GetCmdParam(cmdText);
                var now = DateTime.UtcNow;
                bool hasFilter = !string.IsNullOrEmpty(filter);

                if (hasFilter)
                {
                    devices = [.. devices.Where(d => d.NodeName.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                    channelRegs = [.. channelRegs.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                    deviceApprovals = [.. deviceApprovals.Where(d => d.NodeName.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                    channelApprovals = [.. channelApprovals.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                }

                if (hasFilter
                    && devices.Count == 0
                    && channelRegs.Count == 0
                    && deviceApprovals.Count == 0
                    && channelApprovals.Count == 0
                    && chatSession == null)
                {
                    response.AppendLine($"No registered or approved devices or channels matching filter '{StringHelper.EscapeMdV2(filter)}'\\.");
                    await botClient.SendMessage(chatId, response.ToString().TrimEnd(), parseMode: ParseMode.Markdown);
                    return TgResult.Ok;
                }

                if (chatSession != null)
                {
                    response.AppendLine($"💬 *Active chat session:*");

                    if (chatSession.DeviceId != null)
                    {
                        var device = await registrationService.GetDeviceAsync(chatSession.DeviceId.Value);
                        var hexId = MeshtasticService.GetMeshtasticNodeHexId(chatSession.DeviceId.Value);
                        var network = networks.GetValueOrDefault(device.NetworkId);
                        var name = device?.NodeName ?? hexId;
                        response.AppendLine($"• Device: {StringHelper.EscapeMdV2(name)} `{StringHelper.EscapeMdV2(hexId)}` \\({StringHelper.EscapeMdV2(network?.Name ?? "Unknown")}\\)");
                    }
                    else if (chatSession.ChannelId != null)
                    {
                        var channel = await registrationService.GetChannelAsync(chatSession.ChannelId.Value);
                        var networkName = networks.GetValueOrDefault(channel.NetworkId)?.Name ?? "Unknown";
                        response.AppendLine($"• Channel: {StringHelper.EscapeMdV2(channel.Name)} \\(ID `{channel.Id}`\\), network: {StringHelper.EscapeMdV2(networkName)}");
                    }
                    response.AppendLine();
                }

                if (channelRegs.Count > 0
                    || devices.Count > 0
                    || deviceApprovals.Count > 0
                    || channelApprovals.Count > 0
                    || hasFilter)
                {
                    if (hasFilter || channelRegs.Count > 0 || devices.Count > 0)
                    {
                        var gatewayIdSet = await registrationService.GetGatewaysCached();
                        response.AppendLine($"*{StringHelper.EscapeMdV2(hasFilter ? "Filtered" : "Registered")} channels and devices:*");

                        if (channelRegs.Count > 0)
                        {
                            response.AppendLine();
                            response.AppendLine("*Registered channels:*");
                            foreach (var c in channelRegs)
                            {
                                var networkName = networks.GetValueOrDefault(c.NetworkId)?.Name ?? "Unknown";
                                var singleTag = c.IsSingleDevice ? " \\[Single Device\\]" : "";
                                response.AppendLine($"• *{StringHelper.EscapeMdV2(c.Name)}*{singleTag} \\- ID `{c.Id}`, network: {StringHelper.EscapeMdV2(networkName)}");
                            }
                        }

                        if (devices.Count > 0)
                        {
                            response.AppendLine();
                            response.AppendLine("📟 *Registered devices:*");
                            foreach (var d in devices)
                            {
                                var isGateway = gatewayIdSet.ContainsKey(d.DeviceId);
                                var gatewayTag = isGateway ? " 📡 \\[Gateway\\]" : "";
                                var networkName = networks.GetValueOrDefault(d.NetworkId)?.Name ?? "Unknown";
                                var hexId = MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId);
                                var positionStr = d.LastPositionUpdate != null
                                    ? StringHelper.EscapeMdV2(FormatTimeSpan(now - d.LastPositionUpdate.Value)) + " ago"
                                    : "N/A";
                                response.AppendLine($"• *{StringHelper.EscapeMdV2(d.NodeName)}*{gatewayTag}  `{StringHelper.EscapeMdV2(hexId)}` · {StringHelper.EscapeMdV2(networkName)} · node info: {StringHelper.EscapeMdV2(FormatTimeSpan(now - d.LastNodeInfo))} ago · position: {positionStr}");
                            }
                        }

                    }

                    if (channelApprovals.Count > 0)
                    {
                        response.AppendLine();
                        response.AppendLine($"✅ *Channels approved for chat sessions{(hasFilter ? " \\(filtered\\)" : "")}:*");
                        foreach (var c in channelApprovals)
                        {
                            var networkName = networks.GetValueOrDefault(c.NetworkId)?.Name ?? "Unknown";
                            var singleTag = c.IsSingleDevice ? " \\[Single Device\\]" : "";
                            response.AppendLine($"• *{StringHelper.EscapeMdV2(c.Name)}*{singleTag} \\- ID `{c.Id}`, network: {StringHelper.EscapeMdV2(networkName)}");
                        }
                    }

                    if (deviceApprovals.Count > 0)
                    {
                        response.AppendLine();
                        response.AppendLine($"✅ *Devices approved for chat sessions{(hasFilter ? " \\(filtered\\)" : "")}:*");
                        foreach (var d in deviceApprovals)
                        {
                            var networkName = networks.GetValueOrDefault(d.NetworkId)?.Name ?? "Unknown";
                            var hexId = MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId);
                            response.AppendLine($"• *{StringHelper.EscapeMdV2(d.NodeName)}* `{StringHelper.EscapeMdV2(hexId)}` · {StringHelper.EscapeMdV2(networkName)}");
                        }
                    }
                }

                await botClient.SendMessage(chatId, response.ToString().TrimEnd(), parseMode: ParseMode.MarkdownV2);
            }

            return TgResult.Ok;
        }

        static string FormatTimeSpan(TimeSpan ts)
        {
            return ts.Days > 0
                ? ts.ToString(@"d\.hh\:mm\:ss")
                : ts.ToString(@"hh\:mm\:ss");
        }

        private async Task<TgResult> HandlePosition(long chatId, string cmdText)
        {
            var devices = await registrationService.GetDevicePositionByChatId(chatId);
            if (devices.Count == 0)
            {
                await botClient.SendMessage(chatId, TgBotService.NoDeviceMessage);
            }
            else
            {
                var filter = GetCmdParam(cmdText);
                var now = DateTime.UtcNow;
                bool hasFilter = !string.IsNullOrEmpty(filter);

                if (hasFilter)
                {
                    devices = [.. devices.Where(d => d.NodeName.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                }

                if (hasFilter && devices.Count == 0)
                {
                    await botClient.SendMessage(chatId, $"No registered devices matching filter '{filter}'.");
                    return TgResult.Ok;
                }

                var unknownPositionMsg = new StringBuilder();
                foreach (var d in devices)
                {
                    if (d.LastPositionUpdate == null)
                    {
                        unknownPositionMsg.AppendLine($"• Device: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)})");
                    }
                    else
                    {
                        await botClient.SendMessage(
                            chatId,
                            $"Device: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)}), last position update {FormatTimeSpan(now - d.LastPositionUpdate.Value)} ago:");

                        await botClient.SendLocation(
                            chatId,
                            d.Latitude.Value,
                            d.Longitude.Value,
                            horizontalAccuracy: d.AccuracyMeters.HasValue ? Math.Min(d.AccuracyMeters.Value, 1500) : null,
                            replyParameters: null);
                    }
                }

                if (unknownPositionMsg.Length > 0)
                {
                    await botClient.SendMessage(
                        chatId,
                        "Devices without known position:\r\n" +
                        unknownPositionMsg.ToString());
                }
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedPromoteToGateway_NeedFirmwareConfirm(long userId, long chatId, Message message)
        {
            var text = message.Text?.Trim();
            if (!string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
            {
                var flasherAddress = _options.PublicFlasherAddress;
                var flasherLine = !string.IsNullOrWhiteSpace(flasherAddress)
                    ? $" Flash it at: {flasherAddress}"
                    : string.Empty;

                await botClient.SendMessage(chatId,
                    $"Please flash the custom TMesh firmware on your device first.{flasherLine}\n\n" +
                    "Reply \"*yes*\" once the firmware is installed, or /stop to cancel.",
                    parseMode: ParseMode.Markdown);
                return TgResult.Ok;
            }

            var chatStateWithData = registrationService.GetChatState(userId, chatId);
            if (chatStateWithData?.DeviceId == null)
            {
                await botClient.SendMessage(chatId, "Session data lost. Please start over with /promote_to_gateway.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var deviceId = chatStateWithData.DeviceId.Value;
            registrationService.SetChatState(userId, chatId, ChatState.Default);
            return await ExecutePromoteToGateway(userId, chatId, deviceId);
        }

        private async Task<TgResult> ExecutePromoteToGateway(long userId, long chatId, long deviceId)
        {
            var hexId = MeshtasticService.GetMeshtasticNodeHexId(deviceId);
            var device = await registrationService.GetDeviceAsync(deviceId);
            if (device == null)
            {
                await botClient.SendMessage(chatId, "Device not found. Please start over with /promote_to_gateway.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var deviceName = device.NodeName;

            await registrationService.RegisterGatewayAsync(deviceId, device.NetworkId);
            botCache.StoreGatewayRegistraionChat(deviceId, chatId);


            var mqttUsername = hexId;
            var mqttPassword = registrationService.DeriveMqttPasswordForDevice(deviceId);
            var mqttAddress = _options.PublicMqttAddress;
            var mqttTopic = _options.PublicMqttTopic.Replace(NetworkIdToken, MqttService.NetworkSegmentPrefix + device.NetworkId.ToString());
            var flasherAddress = _options.PublicFlasherAddress;
            var network = await registrationService.GetNetwork(device.NetworkId);
            StringBuilder instructions = CreateGatewaySetupInstructions(
                hexId,
                deviceName,
                mqttUsername,
                mqttPassword,
                mqttAddress,
                mqttTopic,
                flasherAddress,
                network.SaveAnalytics,
                _options.MeshtasticNodeNameLong,
                includeInfoAboutFirstSeenMessage: true);

            await botClient.SendMessage(chatId, instructions.ToString(), parseMode: ParseMode.Markdown);

            return new TgResult([device.NetworkId]);
        }

        public static StringBuilder CreateGatewaySetupInstructions(
            string hexId,
            string deviceName,
            string mqttUsername,
            string mqttPassword,
            string mqttAddress,
            string mqttTopic,
            string flasherAddress,
            bool networkAnalyticsEnabled,
            string botNodeName,
            bool includeInfoAboutFirstSeenMessage)
        {
            var instructions = new StringBuilder();
            instructions.AppendLine($"\u2705 Device *{StringHelper.EscapeMd(deviceName ?? hexId)}* ({hexId}) has been promoted to gateway.");
            instructions.AppendLine();
            instructions.AppendLine("\ud83d\udce1 *MQTT Gateway Setup Instructions*");
            instructions.AppendLine();

            if (!string.IsNullOrWhiteSpace(flasherAddress))
            {
                instructions.AppendLine($"1. If you haven't already, flash the custom TMesh firmware: {flasherAddress}");
                instructions.AppendLine();
                instructions.AppendLine("2. Open your Meshtastic app \u2192 Config \u2192 Network \u2192 MQTT and set the following:");
            }
            else
            {
                instructions.AppendLine("Open your Meshtastic app \u2192 Config \u2192 Network \u2192 MQTT and set the following:");
            }

            instructions.AppendLine();
            instructions.AppendLine($"• *Server address:* `{mqttAddress}`");
            instructions.AppendLine($"• *Username:* `{mqttUsername}`");
            instructions.AppendLine($"• *Password:* `{mqttPassword}`");
            instructions.AppendLine($"• *Root topic:* `{mqttTopic}`");
            instructions.AppendLine($"• *Encryption enabled:* On \u2705");
            instructions.AppendLine($"• *JSON output enabled:* Off \u274c");
            instructions.AppendLine($"• *TLS enabled:* Off \u274c");
            instructions.AppendLine($"• *Map reporting:* On ✅");
            instructions.AppendLine();
            instructions.AppendLine("Other settings:");
            instructions.AppendLine($"• Set Device Role to *Client* in Device settings. If you prefer *Client Mute*, than add {StringHelper.EscapeMd(botNodeName)} node to favorites, set device role to *Client* and rebroadcast mode to *KNOWN_ONLY*.");
            if (networkAnalyticsEnabled)
            {
                instructions.AppendLine("• Enable Device telemetry in Meshtastic settings, this will help to monitor network quality.");
                instructions.AppendLine("• If you have enabled Device telemetry please set Number of Hops in LoRa settings to 7.");
            }
            if (includeInfoAboutFirstSeenMessage)
            {
                instructions.AppendLine();
                instructions.AppendLine("When the first packet will be received by the TMesh from your device, you will get a notification in this chat.");
            }
            return instructions;
        }

        private async Task<TgResult> StartDemoteFromGateway(long userId, long chatId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                await botClient.SendMessage(chatId,
                    "Please send the Meshtastic device ID to demote from gateway. The device ID can be decimal or hex (hex starts with ! or #).");
                registrationService.SetChatState(userId, chatId, ChatState.DemotingFromGateway);
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }

            return await ExecuteDemoteFromGateway(chatId, deviceId);
        }

        private async Task<TgResult> ProceedDemoteFromGateway(long userId, long chatId, Message message)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, "Please send a Meshtastic device ID or /stop to cancel.");
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(text, out var deviceId))
            {
                await botClient.SendMessage(chatId, "Invalid device ID format. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
                return TgResult.Ok;
            }

            registrationService.SetChatState(userId, chatId, ChatState.Default);
            return await ExecuteDemoteFromGateway(chatId, deviceId);
        }

        private async Task<TgResult> ExecuteDemoteFromGateway(long chatId, long deviceId)
        {
            var hexId = MeshtasticService.GetMeshtasticNodeHexId(deviceId);

            // Device must be registered and verified in this chat
            if (!await registrationService.HasDeviceRegistrationAsync(chatId, deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Device {hexId} is not registered in this chat. You can only demote devices registered in this chat.");
                return TgResult.Ok;
            }

            var device = await registrationService.GetDeviceAsync(deviceId);
            var deviceName = device?.NodeName ?? hexId;

            var removed = await registrationService.UnregisterGatewayAsync(deviceId);
            if (removed)
            {
                await botClient.SendMessage(chatId,
                    $"\u2705 Device {deviceName} ({hexId}) has been demoted from gateway.\n\nYou can disconnect it from the MQTT server in your Meshtastic app settings.");
                return new TgResult([device.NetworkId]);
            }
            else
            {
                await botClient.SendMessage(chatId,
                    $"Device {deviceName} ({hexId}) was not registered as a gateway.");
                return TgResult.Ok;
            }
        }


        private async Task<TgResult> AskFirmwareConfirmation(long userId, long chatId, long deviceId)
        {
            var hexId = MeshtasticService.GetMeshtasticNodeHexId(deviceId);

            // Validate device registration before asking for firmware confirmation
            if (!await registrationService.HasDeviceRegistrationAsync(chatId, deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Device {hexId} is not registered in this chat. Please add it first using /add_device.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var device = await registrationService.GetDeviceAsync(deviceId);
            var deviceName = device?.NodeName ?? hexId;
            var flasherAddress = _options.PublicFlasherAddress;
            var flasherLine = !string.IsNullOrWhiteSpace(flasherAddress)
                ? $" You can flash it at: {flasherAddress}"
                : string.Empty;

            registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
            {
                State = ChatState.PromotingToGateway_NeedFirmwareConfirm,
                DeviceId = deviceId
            });

            await botClient.SendMessage(chatId,
                $"Device: *{StringHelper.EscapeMd(deviceName)}* ({hexId})\n\n" +
                $"⚠️ To act as a gateway this device must have custom TMesh firmware installed.{flasherLine}\n\n" +
                "Have you already flashed the TMesh firmware on this device? Reply \"*yes*\" to confirm and receive MQTT setup instructions, or /stop to cancel.",
                parseMode: ParseMode.Markdown);

            return TgResult.Ok;
        }

        private async Task<TgResult> StartPromoteToGateway(long userId, long chatId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                var flasherAddress = _options.PublicFlasherAddress;
                var firmwareLine = !string.IsNullOrWhiteSpace(flasherAddress)
                    ? $"\n\n⚠️ This command requires custom TMesh firmware on your device. Flash it first at: {flasherAddress}"
                    : "\n\n⚠️ This command requires custom TMesh firmware on your device.";

                await botClient.SendMessage(chatId,
                    "Please send the Meshtastic device ID to promote to gateway. The device ID can be decimal or hex (hex starts with ! or #)." +
                    firmwareLine);
                registrationService.SetChatState(userId, chatId, ChatState.PromotingToGateway);
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }

            return await AskFirmwareConfirmation(userId, chatId, deviceId);
        }

        private async Task<TgResult> ProceedPromoteToGateway(long userId, long chatId, Message message)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, "Please send a Meshtastic device ID or /stop to cancel.");
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(text, out var deviceId))
            {
                await botClient.SendMessage(chatId, "Invalid device ID format. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
                return TgResult.Ok;
            }

            return await AskFirmwareConfirmation(userId, chatId, deviceId);
        }

        private Task<TgResult> HandleAdmin(
                  long userId,
                  long chatId,
                  string text)
        {
            return services.GetRequiredService<AdminBotService>()
                .HandleAdmin(userId, chatId, text);
        }



        private async Task<TgResult> ProceedNeedCode(long userId, long chatId, Message message)
        {
            var maybeCode = message.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(maybeCode)
                  && RegistrationService.IsValidCodeFormat(maybeCode))
            {
                if (await registrationService.TryCreateRegistrationWithCode(
                    userId,
                    chatId,
                    maybeCode))
                {
                    await botClient.SendMessage(chatId, "Registration successful.");
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                }
                else
                {
                    await botClient.SendMessage(chatId, "Invalid or expired code. Please check it and try again, or cancel with /stop.");
                }
            }
            else
            {
                await botClient.SendMessage(chatId, "Invalid code format. Please send the 6-digit verification code sent to your Meshtastic device. Send /stop to cancel.");
            }
            return TgResult.Ok;
        }


        private async Task<TgResult> ProceedNeedDeviceId(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && MeshtasticService.TryParseDeviceId(message.Text, out var deviceId))
            {
                return await ProcessDeviceIdForAdd(userId, chatId, deviceId);
            }
            else
            {
                await botClient.SendMessage(chatId, "Invalid device ID. Please send a valid Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedNeedChannelNetwork(long userId, long chatId, Message message, ChatStateWithData state)
        {
            var text = message.Text?.Trim();
            if (!int.TryParse(text, out var networkId))
            {
                await botClient.SendMessage(chatId,
                    "Please reply with the network ID from the list, or /stop to cancel.");
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId,
                    $"Invalid network ID. Please reply with a valid network ID from the list, or /stop to cancel.");
                return TgResult.Ok;
            }

            // Advance to next step depending on what data we already have
            if (string.IsNullOrEmpty(state.ChannelName))
            {
                await botClient.SendMessage(chatId, $"You have selected network \"{network.Name}\".\r\nPlease send your Meshtastic channel name.");
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedName,
                    NetworkId = networkId,
                    PrivacyConfirmed = state.PrivacyConfirmed,
                });
            }
            else if (state.ChannelKey == null)
            {
                await SendNeedChannelKeyTgMsg(chatId);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedKey,
                    ChannelName = state.ChannelName,
                    NetworkId = networkId,
                    PrivacyConfirmed = state.PrivacyConfirmed,
                });
            }
            else
            {
                // Name and key already known (came via command-line), go straight to ProcessChannelForAdd
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedKey,
                    NetworkId = networkId,
                    ChannelName = state.ChannelName,
                    ChannelKey = state.ChannelKey,
                    InsecureKeyConfirmed = state.InsecureKeyConfirmed,
                    IsSingleDevice = state.IsSingleDevice,
                    PrivacyConfirmed = state.PrivacyConfirmed,
                });
                return await ProcessChannelForAdd(
                    userId,
                    chatId,
                    state.ChannelName,
                    state.ChannelKey,
                    MeshtasticService.PskKeyToBase64(state.ChannelKey),
                    state.IsSingleDevice,
                    networkId,
                    state.InsecureKeyConfirmed,
                    state.PrivacyConfirmed);
            }

            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedNeedChannelName(long userId, long chatId, Message message, ChatStateWithData state)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && MeshtasticService.IsValidChannelName(message.Text))
            {
                await SendNeedChannelKeyTgMsg(chatId);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedKey,
                    ChannelName = message.Text,
                    NetworkId = state.NetworkId,
                    PrivacyConfirmed = state.PrivacyConfirmed,
                });
            }
            else
            {
                await botClient.SendMessage(chatId,
                   $"Invalid channel name format: '{message.Text}'. The channel name must be a valid Meshtastic channel name (less than 12 bytes).\n\n" +
                   "Please try again or type /stop to cancel the registration.");
            }
            return TgResult.Ok;
        }
        private async Task<TgResult> ProceedNeedInsecureKeyConfirm(long userId, long chatId, Message message, ChatStateWithData state)
        {
            if (state.InsecureKeyConfirmed || (!string.IsNullOrWhiteSpace(message.Text)
                                && message.Text.Trim().Equals("my key is not secure", StringComparison.InvariantCultureIgnoreCase)))
            {
                if (string.IsNullOrEmpty(state.ChannelName)
                    || !MeshtasticService.IsValidChannelName(state.ChannelName)
                    || !state.NetworkId.HasValue
                    || state.ChannelKey == null)
                {
                    await botClient.SendMessage(chatId,
                        $"Registration data is corrupted, channel name or network is missing in chat state. Registration process aborted. Please try again.");
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                if (await registrationService.IsPublicChannel(state.NetworkId.Value, state.ChannelName, state.ChannelKey))
                {
                    await botClient.SendMessage(chatId,
                                 $"Adding public, well known channels is not allowed. Registration process aborted. Please try with private channels.");
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                return await ProcessChannelForAdd(
                    userId,
                    chatId,
                    state.ChannelName,
                    state.ChannelKey,
                    Convert.ToBase64String(state.ChannelKey),
                    isSingleDevice: null,
                    state.NetworkId,
                    insecureKeyConfirmed: true,
                    state.PrivacyConfirmed);
            }
            else
            {
                await botClient.SendMessage(chatId,
                                $"To confirm that you understand the risks and want to proceed with registering this channel, please reply with `my key is not secure`.\n\n" +
                                "Please try again or type /stop to cancel the registration.", ParseMode.Markdown);
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedNeedChannelKey(long userId, long chatId, Message message, ChatStateWithData state)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && MeshtasticService.TryParseChannelKey(message.Text, out var key))
            {
                if (string.IsNullOrEmpty(state.ChannelName)
                    || !MeshtasticService.IsValidChannelName(state.ChannelName)
                    || !state.NetworkId.HasValue)
                {
                    await botClient.SendMessage(chatId,
                        $"Registration data is corrupted, channel name or network is missing in chat state. Registration process aborted. Please try again.");
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                if (await registrationService.IsPublicChannel(state.NetworkId.Value, state.ChannelName, key))
                {
                    await botClient.SendMessage(chatId,
                                 $"Adding public, well known channels is not allowed. Registration process aborted. Please try with private channels.");
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                if (!state.InsecureKeyConfirmed && MeshtasticService.IsDefaultKey(key))
                {
                    await botClient.SendMessage(chatId,
                                 $"The provided channel key is the default Meshtastic channel key. Anyone will be able to read your messages. This is not secure. Please reply with `my key is not secure` to confirm that you understand the risks and want to proceed with registering this channel, or type /stop to cancel.", ParseMode.Markdown);
                    state.ChannelKey = key;
                    state.State = ChatState.AddingChannel_NeedInsecureKeyConfirm;
                    registrationService.SetChatStateWithData(userId, chatId, state);
                    return TgResult.Ok;
                }

                return await ProcessChannelForAdd(userId,
                    chatId,
                    state.ChannelName,
                    key,
                    message.Text,
                    isSingleDevice: null,
                    state.NetworkId,
                    state.InsecureKeyConfirmed,
                    state.PrivacyConfirmed);
            }
            else
            {
                await botClient.SendMessage(chatId,
                                $"Invalid channel key format: '{message.Text}'. The channel key must be a valid Meshtastic channel key (base64-encoded, 16 or 32 bytes).\n\n" +
                                "Please try again or type /stop to cancel the registration.");
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedAddingChannelNeedPrivacyConfirm(long userId, long chatId, Message message, ChatStateWithData state)
        {
            var text = message.Text?.Trim();
            if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
            {
                if (state.NetworkId == null
                    || string.IsNullOrEmpty(state.ChannelName)
                    || state.ChannelKey == null)
                {
                    return await StartAddChannel(
                        userId,
                        chatId,
                        state.NetworkId?.ToString(),
                        state.ChannelName,
                        state.ChannelKey != null ? Convert.ToBase64String(state.ChannelKey) : null,
                        state.IsSingleDevice.HasValue ? (state.IsSingleDevice.Value ? "single" : "multiple") : null,
                        privacyConfirmed: true);
                }
                else
                {
                    return await ProcessChannelForAdd(
                        userId,
                        chatId,
                        state.ChannelName,
                        state.ChannelKey,
                        Convert.ToBase64String(state.ChannelKey),
                        state.IsSingleDevice,
                        state.NetworkId,
                        state.InsecureKeyConfirmed,
                        privacyConfirmed: true);
                }
            }
            else
            {
                await botClient.SendMessage(chatId,
                                $"To confirm that you understand the privacy risks and want to proceed with registering this channel, please reply with `yes`.\n\n" +
                                "Please try again or type /stop to cancel the registration.", ParseMode.Markdown);
                return TgResult.Ok;
            }
        }

        private async Task<TgResult> ProceedNeedChannelSingleDevice(long userId, long chatId, Message message, ChatStateWithData state)
        {
            var text = message.Text?.Trim();
            bool? isSingleDevice;
            if (string.Equals(text, "single", StringComparison.OrdinalIgnoreCase))
            {
                isSingleDevice = true;
            }
            else if (string.Equals(text, "multiple", StringComparison.OrdinalIgnoreCase))
            {
                isSingleDevice = false;
            }
            else
            {
                await botClient.SendMessage(chatId,
                    "Please reply with *single* or *multiple*, or type /stop to cancel.",
                    parseMode: ParseMode.Markdown);
                return TgResult.Ok;
            }

            return await ProcessChannelForAdd(
                userId,
                chatId,
                state.ChannelName,
                state.ChannelKey,
                MeshtasticService.PskKeyToBase64(state.ChannelKey),
                isSingleDevice,
                state.NetworkId,
                state.InsecureKeyConfirmed,
                state.PrivacyConfirmed);
        }

        private async Task<TgResult> ProcessChannelForAdd(
            long userId,
            long chatId,
            string channelName,
            byte[] channelKey,
            string channelKeyBase64,
            bool? isSingleDevice,
            int? networkId,
            bool insecureKeyConfirmed,
            bool privacyConfirmed)
        {
            if (networkId == null)
            {
                await botClient.SendMessage(chatId,
                    $"Registration data is corrupted, network is missing in chat state. Registration process aborted. Please try again.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var dbChannel = await registrationService.FindChannelAsync(networkId.Value, channelName, channelKey);
            if (dbChannel != null && await registrationService.HasChannelRegistrationAsync(chatId, dbChannel.Id))
            {
                await botClient.SendMessage(chatId, $"Channel {channelName} with same key is already registered in this chat. Registration aborted.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            if (!privacyConfirmed)
            {
                var tgChat = await registrationService.GetTgChatByChatIdAsync(chatId);
                var privacyRes = await AddingChannelMaybeShowPrivacyDisclaimer(
                      userId,
                      chatId,
                      tgChat,
                  networkId: networkId,
                  channelName: channelName,
                  isSingleDevice: isSingleDevice,
                  channelKey: channelKey,
                  insecureKeyConfirmed: insecureKeyConfirmed);

                if (privacyRes != null)
                {
                    return privacyRes;
                }
            }

            // If channel doesn't exist yet and isSingleDevice not yet decided, ask
            if (dbChannel == null && !isSingleDevice.HasValue)
            {
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedSingleDevice,
                    ChannelName = channelName,
                    ChannelKey = channelKey,
                    IsSingleDevice = null,
                    NetworkId = networkId,
                    InsecureKeyConfirmed = insecureKeyConfirmed,
                    PrivacyConfirmed = privacyConfirmed
                });

                await botClient.SendMessage(chatId,
                    "Is this channel used by a single device only?\n\n" +
                    "• Reply *single* - Optimized routing will be used (works only for single device channels)\n" +
                    "• Reply *multiple* - Standard broadcast routing will be used\n\n" +
                    "Type /stop to cancel.",
                    parseMode: ParseMode.Markdown);
                return TgResult.Ok;
            }

            if (dbChannel == null &&
                !insecureKeyConfirmed &&
                 MeshtasticService.IsDefaultKey(channelKey))
            {
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedInsecureKeyConfirm,
                    ChannelName = channelName,
                    ChannelKey = channelKey,
                    IsSingleDevice = isSingleDevice,
                    NetworkId = networkId,
                    InsecureKeyConfirmed = insecureKeyConfirmed,
                    PrivacyConfirmed = privacyConfirmed
                });
                await botClient.SendMessage(chatId,
                    $"The provided channel key is the default Meshtastic channel key. Anyone will be able to read your messages. This is not secure. Please reply with 'my key is not secure' to confirm that you understand the risks and want to proceed with registering this channel, or type /stop to cancel.");
                return TgResult.Ok;
            }

            var codesSent = registrationService.IncrementChannelCodesSentRecently(channelName, channelKeyBase64);
            if (codesSent > RegistrationService.MaxCodeVerificationTries)
            {
                await botClient.SendMessage(chatId, $"Channel {channelName} has reached the maximum number of verification codes sent. Please wait at least 1 hour before trying again to add the same channel to any chats. Registration aborted.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var code = RegistrationService.GenerateRandomCode();
            registrationService.StoreChannelPendingCodeAsync(userId, chatId, channelName, channelKey, networkId.Value, isSingleDevice, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await botClient.SendMessage(chatId,
                $"Verification code sent to channel {channelName}. Please reply with the received code here. The code is valid for 5 minutes.");

            // Use existing channel's networkId if it already exists in DB, otherwise use the one chosen during registration
            var resolvedNetworkId = dbChannel?.NetworkId ?? networkId!.Value;

            var channel = new ChannelKey
            {
                NetworkId = resolvedNetworkId,
                ChannelXor = MeshtasticService.GenerateChannelHash(channelName, channelKey),
                PreSharedKey = channelKey,
                IsSingleDevice = isSingleDevice ?? false
            };

            registrationService.SetChatState(userId, chatId, ChatState.AddingChannel_NeedCode);

            return new TgResult(new OutgoingTextMessage
            {
                Recipient = channel,
                TelegramChatId = chatId,
                TelegramMessageId = msg.MessageId,
                Text = $"TMesh verification code is: {code}"
            });
        }

        private async Task<TgResult> ProcessDeviceIdForAdd(long userId, long chatId, long deviceId)
        {
            var device = await registrationService.GetDeviceAsync(deviceId);
            if (device == null)
            {
                await botClient.SendMessage(chatId,
                    $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has not yet been seen by the {_options.MeshtasticNodeNameLong} node in the Meshtastic network.\r\n" +
                    $"1. Check that you are located in supported city (network) with /list_networks command.\r\n" +
                    "2. Verify that primary channel on you device is configured correctly (see correct channels in /list_networks).\r\n" +
                    $"3. Find node {_options.MeshtasticNodeNameLong} in the node list, open it and click on 'Exchange user information'. {_options.MeshtasticNodeNameLong} broadcasts it's node info every {_options.SentTBotNodeInfoEverySeconds / 60} minutes.\r\n\r\n" +
                    "Registration aborted.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            if (await registrationService.HasDeviceRegistrationAsync(chatId, deviceId))
            {
                await botClient.SendMessage(chatId, $"Device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}) is already registered in this chat. Registration aborted.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var codesSent = registrationService.IncrementDeviceCodesSentRecently(deviceId);
            if (codesSent > RegistrationService.MaxCodeVerificationTries)
            {
                await botClient.SendMessage(chatId, $"Device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}) has reached the maximum number of verification codes sent. Please wait at least 1 hour before trying again to add the same device to any chats. Registration aborted.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var code = RegistrationService.GenerateRandomCode();
            registrationService.StoreDevicePendingCodeAsync(userId, chatId, deviceId, device.NetworkId, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await botClient.SendMessage(chatId,
                $"Verification code sent to device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}). Please reply with the received code here. The code is valid for 5 minutes.");

            registrationService.SetChatState(userId, chatId, ChatState.AddingDevice_NeedCode);

            return new TgResult(new OutgoingTextMessage
            {
                Recipient = device,
                TelegramChatId = chatId,
                TelegramMessageId = msg.MessageId,
                Text = $"TMesh verification code is: {code}"
            });
        }

        private async Task<TgResult> ProceedKillingChatNeedConfirm(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && message.Text.Trim().Equals("yes", StringComparison.InvariantCultureIgnoreCase))
            {
                await registrationService.RemoveAllForTgChat(chatId);
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                await botClient.SendMessage(chatId, "Chat has been removed. All registrations and approvals have been removed and the chat will no longer receive any messages from the bot. To start using the bot again use /start command.");
                return TgResult.Ok;
            }
            else
            {
                await botClient.SendMessage(chatId,
                    $"To confirm that you want to remove this chat, please reply with `yes`.\n\n" +
                    "Please try again or type /stop to cancel.", ParseMode.Markdown);
                return TgResult.Ok;
            }
        }

        private async Task<TgResult> ProceedStarting_NeedPrivacyConfirm(long userId, long chatId, Message message, ChatStateWithData state)
        {
            if (state.PrivacyConfirmed || (!string.IsNullOrWhiteSpace(message.Text)
                                && message.Text.Trim().Equals("yes", StringComparison.InvariantCultureIgnoreCase)))
            {
                return await ProceedStart(userId, chatId, message);
            }
            else
            {
                await botClient.SendMessage(chatId,
                    $"To confirm that you have read and understood the privacy notice, please reply with `yes`.\n\n" +
                    "Please try again or type /stop to cancel.", ParseMode.Markdown);
                return TgResult.Ok;
            }
        }

        private async Task<TgResult> ProceedAddingDeviceNeedPrivacyConfirm(long userId, long chatId, Message message, ChatStateWithData state)
        {
            if (state.PrivacyConfirmed || (!string.IsNullOrWhiteSpace(message.Text)
                                && message.Text.Trim().Equals("yes", StringComparison.InvariantCultureIgnoreCase)))
            {
                if (state.DeviceId == null)
                {
                    return await SendAddingDeviceNeedId(userId, chatId);
                }
                state.PrivacyConfirmed = true;
                return await ProcessDeviceIdForAdd(userId, chatId, state.DeviceId.Value);
            }
            else
            {
                await botClient.SendMessage(chatId,
                    $"To confirm that you have read and understood the privacy notice, please reply with `yes`.\n\n" +
                    "Please try again or type /stop to cancel.", ParseMode.Markdown);
                return TgResult.Ok;
            }
        }

        private async Task<TgResult> ProceedDeviceAdd(
           long userId,
           long chatId,
           Message message,
           ChatStateWithData chatState)
        {
            if (chatState.State == ChatState.AddingDevice_NeedId)
            {
                return await ProceedNeedDeviceId(userId, chatId, message);
            }
            else if (chatState.State == ChatState.AddingDevice_NeedCode)
            {
                return await ProceedNeedCode(userId, chatId, message);
            }
            else if (chatState.State == ChatState.AddingDevice_NeedPrivacyConfim)
            {
                return await ProceedAddingDeviceNeedPrivacyConfirm(userId, chatId, message, chatState);
            }
            else
            {
                logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
                return TgResult.Ok;
            }
        }

        private async Task<TgResult> ProceedDeviceRemove(long userId, long chatId, Message message, bool isRemoveFromAll)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, "Please send a Meshtastic device ID to remove or /stop to cancel.");
                return TgResult.Ok;
            }
            if (!MeshtasticService.TryParseDeviceId(text, out var deviceId))
            {
                await botClient.SendMessage(chatId, "Invalid device ID format. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
                return TgResult.Ok;
            }

            await ExecuteRemoveDevice(chatId, deviceId, isRemoveFromAll);
            registrationService.SetChatState(userId, chatId, ChatState.Default);
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedChannelRemove(long userId, long chatId, Message message, bool isRemoveFromAll)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, "Please send a Channel ID to remove or /stop to cancel.");
                return TgResult.Ok;
            }
            if (!int.TryParse(text, out var channelId))
            {
                await botClient.SendMessage(chatId, "Invalid channel ID format. The channel ID must be a valid integer. Send /stop to cancel.");
                return TgResult.Ok;
            }

            await ExecuteRemoveChannel(chatId, userId, channelId, isRemoveFromAll);
            registrationService.SetChatState(userId, chatId, ChatState.Default);
            return TgResult.Ok;
        }


        public async Task<TgResult> ProcessChatWithState(Message msg, ChatStateWithData chatStateWithData)
        {
            var chatId = msg.Chat.Id;
            var userId = msg.From.Id;

            if (msg.Text?.StartsWith("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Operation canceled.");
                var newChatState = chatStateWithData != null && chatStateWithData.State == ChatState.Admin
                    ? ChatState.Admin
                    : ChatState.Default;
                registrationService.SetChatState(userId, chatId, newChatState);
                return TgResult.Ok;
            }

            switch (chatStateWithData?.State)
            {
                case ChatState.Starting_NeedPrivacyConfim:
                    return await ProceedStarting_NeedPrivacyConfirm(userId, chatId, msg, chatStateWithData);
                case ChatState.KillingChat_NeedConfirm:
                    return await ProceedKillingChatNeedConfirm(userId, chatId, msg);
                case ChatState.AddingDevice_NeedPrivacyConfim:
                case ChatState.AddingDevice_NeedId:
                case ChatState.AddingDevice_NeedCode:
                    return await ProceedDeviceAdd(userId, chatId, msg, chatStateWithData);
                case ChatState.AddingChannel_NeedPrivacyConfim:
                case ChatState.AddingChannel_NeedNetwork:
                case ChatState.AddingChannel_NeedName:
                case ChatState.AddingChannel_NeedKey:
                case ChatState.AddingChannel_NeedInsecureKeyConfirm:
                case ChatState.AddingChannel_NeedSingleDevice:
                case ChatState.AddingChannel_NeedCode:
                    return await ProceedChannelAdd(userId, chatId, msg, chatStateWithData);
                case ChatState.RemovingDevice:
                case ChatState.RemovingDeviceFromAll:
                    return await ProceedDeviceRemove(userId, chatId, msg, isRemoveFromAll: chatStateWithData.State == ChatState.RemovingDeviceFromAll);
                case ChatState.RemovingChannel:
                case ChatState.RemovingChannelFromAll:
                    {
                        return await ProceedChannelRemove(userId, chatId, msg, isRemoveFromAll: chatStateWithData.State == ChatState.RemovingChannelFromAll);
                    }
                case ChatState.PromotingToGateway:
                    return await ProceedPromoteToGateway(userId, chatId, msg);
                case ChatState.PromotingToGateway_NeedFirmwareConfirm:
                    return await ProceedPromoteToGateway_NeedFirmwareConfirm(userId, chatId, msg);
                case ChatState.DemotingFromGateway:
                    return await ProceedDemoteFromGateway(userId, chatId, msg);
                case ChatState.RegisteringChat_NeedName:
                    return await ProceedRegisteringChatNeedName(userId, chatId, msg);
                default:
                    throw new InvalidOperationException($"Unexpected chat state {chatStateWithData} in ProcessCommandChat");
            }
        }

        private async Task<TgResult> ProceedRegisteringChatNeedName(long userId, long chatId, Message msg)
        {
            var name = msg.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                await botClient.SendMessage(chatId, "Please provide a valid name for the chat.");
                return TgResult.Ok;
            }

            bool containsSpaces = name.Contains(' ');
            bool tooLong = name.Length > TBotDbContext.MaxChatNameLength;
            bool startsWithAt = name.StartsWith('@');

            if (containsSpaces || tooLong || startsWithAt)
            {
                var error = new StringBuilder("Invalid chat name. ");
                if (containsSpaces)
                    error.Append("Chat name can't contain spaces. ");
                if (tooLong)
                    error.Append($"Chat name is too long. Maximum length is {TBotDbContext.MaxChatNameLength} characters. ");
                if (startsWithAt)
                    error.Append("Chat name can't start with '@' character. ");

                error.Append("Please choose a different name.");
                await botClient.SendMessage(chatId, error.ToString());
                return TgResult.Ok;
            }

            var normalized = RegistrationService.NormalizeChatName(name, isPrivate: false);

            var tgChat = await registrationService.GetTgChatByNameAsync(normalized);
            if (tgChat != null && tgChat.ChatId != chatId)
            {
                await botClient.SendMessage(chatId, $"Chat name '{name}' is already taken. Please choose a different name.");
                return TgResult.Ok;
            }

            // Proceed with the registration using the provided name
            tgChat = await registrationService.RegisterTgChatAsync(chatId, name, isPrivate: false);
            registrationService.SetChatState(userId, chatId, ChatState.Default);
            await botClient.SendMessage(chatId, $"✅ Chat is registered successfully with name {tgChat.ChatName}. Devices can now send requests to start chat with this group using /chat {tgChat.ChatName} command via {_options.MeshtasticNodeNameLong} node or private channel registered with TMesh.");
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedChannelAdd(
           long userId,
           long chatId,
           Message message,
           ChatStateWithData chatState)
        {
            if (chatState.State == ChatState.AddingChannel_NeedNetwork)
            {
                return await ProceedNeedChannelNetwork(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedName)
            {
                return await ProceedNeedChannelName(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedKey)
            {
                return await ProceedNeedChannelKey(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedInsecureKeyConfirm)
            {
                return await ProceedNeedInsecureKeyConfirm(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedSingleDevice)
            {
                return await ProceedNeedChannelSingleDevice(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedCode)
            {
                return await ProceedNeedCode(userId, chatId, message);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedPrivacyConfim)
            {
                return await ProceedAddingChannelNeedPrivacyConfirm(userId, chatId, message, chatState);
            }
            else
            {
                logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
                return TgResult.Ok;
            }
        }



        private async Task<TgResult> StartAddChannel(
            long userId,
            long chatId,
            string networkIdText,
            string channelNameText,
            string channelKey,
            string mode = null,
            bool privacyConfirmed = false)
        {
            var tgChat = await registrationService.GetTgChatByChatIdAsync(chatId);
            // Parse optional mode argument early so we can store it in state if network selection is needed
            bool? isSingleDevice = null;
            if (!string.IsNullOrWhiteSpace(mode))
            {
                if (string.Equals(mode, "single", StringComparison.OrdinalIgnoreCase))
                    isSingleDevice = true;
                else if (string.Equals(mode, "multiple", StringComparison.OrdinalIgnoreCase))
                    isSingleDevice = false;
                else
                {
                    await botClient.SendMessage(chatId,
                        $"Invalid mode '{mode}'. Use 'single' or 'multiple' as the third argument.");
                    return TgResult.Ok;
                }
            }

            var networks = await registrationService.GetNetworksCached();

            int networkId;
            if (string.IsNullOrEmpty(networkIdText))
            {
                if (!privacyConfirmed)
                {
                    var privacyRes = await AddingChannelMaybeShowPrivacyDisclaimer(
                        userId,
                        chatId,
                        tgChat,
                        networkId: null,
                        channelName: null,
                        isSingleDevice: isSingleDevice);

                    if (privacyRes != null)
                    {
                        return privacyRes;
                    }
                }

                var sb = new StringBuilder("Please select a network by replying with its ID:\n");
                foreach (var n in networks)
                {
                    sb.AppendLine($"*{StringHelper.EscapeMd(n.Name)}* - ID `{n.Id}`");
                }
                sb.AppendLine();
                sb.AppendLine($"_If your city is not listed and you are ready to convert your device to a TMesh gateway, please contact the administrator - {StringHelper.EscapeMd(_options.AdminTgContact)}_");
                sb.AppendLine("\nType /stop to cancel.");

                await botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Markdown);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedNetwork,
                    PrivacyConfirmed = true,
                });
                return TgResult.Ok;
            }
            else
            {
                if (!int.TryParse(networkIdText, out networkId))
                {
                    await botClient.SendMessage(chatId,
                        $"Invalid network ID format: '{networkIdText}'. The network ID must be a valid integer.\n\n" +
                        "Examples:\n" +
                        "• /add_channel 123 MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= single\n" +
                        "• /add_channel 123 MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= multiple\n" +
                        "Or use /add_channel without parameters and I'll ask for the network, channel name and key.");
                    return TgResult.Ok;
                }

                if (!networks.Any(x => x.Id == networkId))
                {
                    var sb = new StringBuilder($"Network ID {networkId} not found. Please make sure to provide a valid network ID.\n\n*Available networks:*\n");
                    foreach (var n in networks)
                        sb.AppendLine($"*{StringHelper.EscapeMd(n.Name)}* - ID `{n.Id}`");
                    sb.AppendLine("\nPlease reply with valid network ID or type /stop to cancel the registration.");
                    await botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Markdown);
                    return TgResult.Ok;
                }
            }

            if (string.IsNullOrWhiteSpace(channelNameText))
            {
                if (!privacyConfirmed)
                {
                    var privacyRes = await AddingChannelMaybeShowPrivacyDisclaimer(
                        userId,
                        chatId,
                        tgChat,
                        networkId: networkId,
                        channelName: null,
                        isSingleDevice: isSingleDevice);

                    if (privacyRes != null)
                    {
                        return privacyRes;
                    }
                }

                // No channel name provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic channel name.");
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedName,
                    NetworkId = networkId,
                    PrivacyConfirmed = true,
                });
                return TgResult.Ok;
            }

            // Channel name provided in command, validate it
            if (!MeshtasticService.IsValidChannelName(channelNameText))
            {
                await botClient.SendMessage(chatId,
                     $"Invalid channel name format: '{channelNameText}'. The channel name must be a valid Meshtastic channel name (less than 12 bytes).\n\n" +
                     "Examples:\n" +
                     "• /add_channel 123 MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= single\n" +
                     "• /add_channel 123 MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= multiple\n" +
                     "Or use /add_channel without parameters and I'll ask for the channel name and key.");
                return TgResult.Ok;
            }

            if (string.IsNullOrEmpty(channelKey))
            {
                if (!privacyConfirmed)
                {
                    var privacyRes = await AddingChannelMaybeShowPrivacyDisclaimer(
                        userId,
                        chatId,
                        tgChat,
                        networkId: networkId,
                        channelName: channelNameText,
                        isSingleDevice: isSingleDevice);

                    if (privacyRes != null)
                    {
                        return privacyRes;
                    }
                }

                await SendNeedChannelKeyTgMsg(chatId);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedKey,
                    ChannelName = channelNameText,
                    NetworkId = networkId,
                    PrivacyConfirmed = true
                });
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseChannelKey(channelKey, out var keyBytesForSingle))
            {
                await botClient.SendMessage(chatId,
                             $"Invalid channel key format: '{channelKey}'. The channel key must be a valid Meshtastic channel key (base64-encoded, 16 or 32 bytes).\n\n" +
                             "Examples:\n" +
                             "• /add_channel 123 MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= single\n" +
                             "Or use /add_channel without parameters and I'll ask for the channel name and key.");
                return TgResult.Ok;
            }

            if (await registrationService.IsPublicChannel(networkId, channelNameText, keyBytesForSingle))
            {
                await botClient.SendMessage(chatId,
                             $"Adding public, well-known channels is not allowed.");
                return TgResult.Ok;
            }

            return await ProcessChannelForAdd(
                userId,
                chatId,
                channelNameText,
                keyBytesForSingle,
                channelKey,
                isSingleDevice,
                networkId,
                insecureKeyConfirmed: false,
                privacyConfirmed: privacyConfirmed);
        }

        private async Task<TgResult> AddingChannelMaybeShowPrivacyDisclaimer(
            long userId,
            long chatId,
            TgChat tgChat,
            int? networkId,
            string channelName,
            bool? isSingleDevice,
            byte[] channelKey = null,
            bool insecureKeyConfirmed = false)
        {
            if (tgChat == null && !string.IsNullOrEmpty(_options.Texts?.PrivacyDisclaimer))
            {
                var msgMd = GetPrivacyDisclaimerMdMessage(_options.Texts.PrivacyDisclaimer);
                await botClient.SendMessage(chatId, msgMd, ParseMode.Markdown);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedPrivacyConfim,
                    PrivacyConfirmed = false,
                    NetworkId = networkId,
                    ChannelName = channelName,
                    IsSingleDevice = isSingleDevice,
                    ChannelKey = channelKey,
                    InsecureKeyConfirmed = insecureKeyConfirmed
                });
                return TgResult.Ok;
            }

            return TgResult.Ok;
        }

        private async Task SendNeedChannelKeyTgMsg(long chatId)
        {
            await botClient.SendMessage(chatId, "Please send your Meshtastic channel key (base64-encoded, 16 or 32 bytes).");
        }


        private async Task<TgResult> StartAddDevice(long userId, long chatId, string deviceIdText)
        {
            var tgChat = await registrationService.GetTgChatByChatIdAsync(chatId);
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                if (tgChat == null && !string.IsNullOrEmpty(_options.Texts?.PrivacyDisclaimer))
                {
                    var msgMd = GetPrivacyDisclaimerMdMessage(_options.Texts.PrivacyDisclaimer);
                    await botClient.SendMessage(chatId, msgMd, ParseMode.Markdown);
                    registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                    {
                        State = ChatState.AddingDevice_NeedPrivacyConfim,
                        PrivacyConfirmed = false
                    });
                    return TgResult.Ok;
                }

                return await SendAddingDeviceNeedId(userId, chatId);
            }

            // Device ID provided in command, process immediately
            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).\n\n" +
                    "Examples:\n" +
                    "• /add_device 123456789\n" +
                    "• /add_device !75bcd15\n" +
                    "• /add_device #75bcd15\n\n" +
                    "Or use /add_device without parameters and I'll ask for the device ID.");
                return TgResult.Ok;
            }

            if (tgChat == null && !string.IsNullOrEmpty(_options.Texts?.PrivacyDisclaimer))
            {
                var msgMd = GetPrivacyDisclaimerMdMessage(_options.Texts.PrivacyDisclaimer);
                await botClient.SendMessage(chatId, msgMd, ParseMode.Markdown);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingDevice_NeedPrivacyConfim,
                    DeviceId = deviceId,
                    PrivacyConfirmed = false
                });
                return TgResult.Ok;
            }

            // Process the device ID (same logic as ProcessNeedDeviceId)
            return await ProcessDeviceIdForAdd(userId, chatId, deviceId);
        }

        private async Task<TgResult> SendAddingDeviceNeedId(long userId, long chatId)
        {
            await botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
            registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
            {
                State = ChatState.AddingDevice_NeedId,
                PrivacyConfirmed = true
            });
            return TgResult.Ok;
        }

        private async Task<TgResult> StartRemoveDevice(long userId, long chatId, string deviceIdText, bool isRemoveFromAll)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                // No device ID provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
                registrationService.SetChatState(userId, chatId,
                    isRemoveFromAll ? ChatState.RemovingDeviceFromAll : ChatState.RemovingDevice);
                return TgResult.Ok;
            }

            // Device ID provided in command, process immediately
            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).\n\n" +
                    "Examples:\n" +
                    "• /remove_device 123456789\n" +
                    "• /remove_device !75bcd15\n" +
                    "• /remove_device #75bcd15\n\n" +
                    "Or use /remove_device without parameters and I'll ask for the device ID.");
                return TgResult.Ok;
            }

            await ExecuteRemoveDevice(chatId, deviceId, isRemoveFromAll);
            return TgResult.Ok;
        }


        private async Task<TgResult> StartRemoveChannel(long userId, long chatId, long telegramUserId, string channelIdText, bool isRemoveFromAll)
        {
            if (string.IsNullOrWhiteSpace(channelIdText))
            {
                var channelRegs = await registrationService.GetChannelNamesByChatId(chatId);
                var approvals = await registrationService.GetChannelApprovalsByChatId(chatId);
                if (channelRegs.Count == 0
                    && approvals.Count == 0)
                {
                    await botClient.SendMessage(chatId, "No channels are registered or approved in this chat.");
                    return TgResult.Ok;
                }
                var sb = new StringBuilder();
                if (channelRegs.Count > 0)
                {
                    sb.AppendLine("Registered channels:");
                    var lines = channelRegs.Select(c => $"• {c.Name} (ID {c.Id})");
                    lines.ToList().ForEach(l => sb.AppendLine(l));
                }
                if (approvals.Count > 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                    }
                    sb.AppendLine("Approved channels:");
                    var lines = approvals.Select(a => $"• {a.Name} (ID {a.Id})");
                    lines.ToList().ForEach(l => sb.AppendLine(l));
                }

                sb.AppendLine("Please send the ID of the channel you want to remove.");

                // No channel ID provided, ask for it
                await botClient.SendMessage(chatId, sb.ToString());
                registrationService.SetChatState(userId, chatId,
                    isRemoveFromAll ? ChatState.RemovingChannelFromAll : ChatState.RemovingChannel);
                return TgResult.Ok;
            }

            // Channel ID provided in command, process immediately
            if (!int.TryParse(channelIdText, out var channelId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid channel ID format: '{channelIdText}'. The channel ID must be a valid integer.\n\nPlease use remove command without params to see ids of registered channels\r\n" +
                    "Examples:\n" +
                    "• /remove_channel 123456789\n" +
                    "Or use /remove_channel without parameters and I'll ask for the channel ID.");
                return TgResult.Ok;
            }

            await ExecuteRemoveChannel(chatId, telegramUserId, channelId, isRemoveFromAll);
            return TgResult.Ok;
        }

        private async Task ExecuteRemoveDevice(long chatId, long deviceId, bool isRemoveFromAll)
        {
            if (isRemoveFromAll)
            {
                // Process removal from all chats
                var removedFromAll = await registrationService.RemoveDeviceFromAllChatsViaOneChatAsync(chatId, deviceId);
                if (!removedFromAll)
                {
                    await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} is not registered in this chat. To prove ownership of device please register it first in this chat then retry the command.");
                }
                else
                {
                    await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has been removed from all chats.");
                }
            }
            else
            {
                // Process removal immediately
                var removed = await registrationService.RemoveDeviceFromChatAsync(chatId, deviceId);
                if (!removed)
                {
                    await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} is not registered in this chat.");
                }
                else
                {
                    await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has been removed from this chat.");
                }
            }
        }


        private async Task ExecuteRemoveChannel(long chatId, long telegramUserId, int channelId, bool isRemoveFromAll)
        {
            if (isRemoveFromAll)
            {
                // Process removal from all chats
                var (removedFromCurrentChat, removedFromOtherChats) = await registrationService.RemoveChannelFromAllChatsViaOneChatAsync(chatId, telegramUserId, channelId);
                if (!removedFromCurrentChat)
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} is not registered in this chat. To prove that you still have access to the channel please register it first in this chat then retry the command.");
                }
                else if (removedFromOtherChats)
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} has been removed from this chat and all other chats where you have registered it. Registration of the channel created by other Telegram users in other chats are not removed.");
                }
                else
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} has been removed from this chat.");
                }
            }
            else
            {
                // Process removal immediately
                var removed = await registrationService.RemoveChannelFromChat(chatId, channelId);
                if (!removed)
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} is not registered in this chat.");
                }
                else
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} has been removed from this chat.");
                }
            }
        }



        private string ExtractSingleArgFromCommand(string commandText, string command)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return null;
            }

            // Remove the command part and trim
            var text = commandText.AsSpan()[command.Length..].Trim();
            var botName = $"@{_options.TelegramBotUserName}";

            if (text.StartsWith(botName))
            {
                text = text[botName.Length..].Trim();
            }

            if (text.Length == 0)
            {
                return null;
            }

            return text.ToString();
        }

        private (string networkId, string name, string key, string mode) ExtractChannelFromCommand(string commandText, string command)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return default;
            }

            // Remove the command part and trim
            var text = commandText[command.Length..].Trim();
            if (text == $"@{_options.TelegramBotUserName}")
            {
                return default;
            }

            // Return null if empty (no device ID provided)
            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            // Extract parts
            var parts = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return default;
            }
            else if (parts.Length == 1)
            {
                return (parts[0], null, null, null);
            }
            else if (parts.Length == 2)
            {
                return (parts[0], parts[1], null, null);
            }
            else if (parts.Length == 3)
            {
                return (parts[0], parts[1], parts[2], null);
            }
            else
            {
                // More than 3 parts, treat the rest as mode (to allow spaces in channel name)
                var mode = string.Join(' ', parts.Skip(3));
                return (parts[0], parts[1], parts[2], mode);
            }
        }

        private async Task<TgResult> HandleKill(long userId, long chatId, Message message)
        {
            await botClient.SendMessage(chatId, "Please confirm that you want to delete this chat from TMesh.\n" +
                "This will delete chat, delete all registered devices and channels, delete approved devices and channels.\n" +
                "If you want to disable new chat requests from unknown devices and channels toy can use /disable command instead of /kill.\n\n" +
                "To confirm please reply with `yes` or /stop to canel the operation.");

            registrationService.SetChatState(userId, chatId, ChatState.KillingChat_NeedConfirm);

            return TgResult.Ok;
        }

        private async Task<TgResult> HandleStart(long userId, long chatId, Message message)
        {
            var tgChat = await registrationService.GetTgChatByChatIdAsync(chatId);
            var privacyDisclaimerMsg = _options.Texts?.PrivacyDisclaimer;
            if (tgChat == null && !string.IsNullOrEmpty(privacyDisclaimerMsg))
            {
                string msgMd = GetPrivacyDisclaimerMdMessage(privacyDisclaimerMsg);
                await botClient.SendMessage(chatId, msgMd, ParseMode.Markdown);
                registrationService.SetChatState(userId, chatId, ChatState.Starting_NeedPrivacyConfim);
                return TgResult.Ok;
            }
            else
            {
                return await ProceedStart(userId, chatId, message);
            }
        }

        private static string GetPrivacyDisclaimerMdMessage(string privacyDisclaimerMsg)
        {
            var msg = new StringBuilder();
            msg.AppendLine("Welcome to the TMesh Telegram bot! This bot allows you to connect your Telegram account with Meshtastic devices and channels, enabling seamless communication between them.");
            msg.AppendLine("Please read carefully the privacy disclaimer:");
            msg.AppendLine();
            msg.Append("*");
            msg.Append(StringHelper.EscapeMd(privacyDisclaimerMsg));
            msg.AppendLine("*");
            msg.AppendLine();
            msg.AppendLine("Please reply with *yes* to start using the bot or /stop to cancel.");
            return msg.ToString();
        }

        private async Task<TgResult> ProceedStart(long userId, long chatId, Message message)
        {
            bool isPrivateChat = message.Chat.Type == ChatType.Private || message.Chat.Type == ChatType.Sender;
            if (!isPrivateChat)
            {
                var groupChat = await registrationService.GetTgChatByChatIdAsync(chatId);
                if (groupChat != null)
                {
                    if (groupChat.IsActive)
                    {
                        await botClient.SendMessage(chatId, $"This group chat is already registered and active with name *{StringHelper.EscapeMd(groupChat.ChatName)}*. Meshtastic devices can reach it using `/chat {StringHelper.EscapeMd(groupChat.ChatName)}` command via {StringHelper.EscapeMd(_options.MeshtasticNodeNameLong)} or private channel registered with TMesh. If you want to change group name, disable it with /disable command and start again with new name.", ParseMode.Markdown);
                        return TgResult.Ok;
                    }
                    //else
                    //{
                    //    //let bot ask for new name
                    //}
                }
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    PrivacyConfirmed = true,
                    State = ChatState.RegisteringChat_NeedName
                });
                await botClient.SendMessage(chatId, $"This chat is group chat. Please provide a unique name for the chat. Name should have no spaces and contain less than {TBotDbContext.MaxChatNameLength} characters. Name can't start with '@'. Meshtastic devices will be able to connect to this chat with /chat <your_chat_name> command via {_options.MeshtasticNodeNameLong} node or private channel registered with TMesh.");
                return TgResult.Ok;
            }

            if (string.IsNullOrEmpty(message.From?.Username))
            {
                await botClient.SendMessage(chatId, "To use chat features you need to set a Telegram username for your account in Telegram settings. Please set a username and then use /start command again.");
                return TgResult.Ok;
            }

            var username = message.From.Username;
            var tgChat = await registrationService.RegisterTgChatAsync(chatId, message.From.Username, isPrivate: true);

            registrationService.SetChatState(userId, chatId, ChatState.Default);

            await botClient.SendMessage(chatId,
                $"✅ Your Telegram chat is now registered with TMesh\\!\n\n" +
                $"🔹 Meshtastic devices and channels can now initiate a chat with you using:\n" +
                $"  `/chat {StringHelper.EscapeMdV2(tgChat.ChatName)}` via {StringHelper.EscapeMdV2(_options.MeshtasticNodeNameLong)} or private channel registered with TMesh\\.\n\n" +
                $"🔹 You can also start a chat with any Meshtastic device:\n" +
                $"  `/chat \\!\\<deviceId\\>`\n" +
                $"  Example: `/chat \\!75bcd15`\n\n" +
                $"🔹 To end an active chat session:\n" +
                $"  `/end\\_chat` \\- ends current chat session\n" +
                $"🔹 To disable your chat from receiving new chat request from Meshtastic \\(already approved devices and channels still will be able to start chat sessions\\):\n" +
                $"  `/disable`\n\n" +
                $"You can also use `/add\\_device` and `/add\\_channel` commands to register devices and channels for permanent messaging\\.",
                parseMode: ParseMode.MarkdownV2);

            return TgResult.Ok;
        }

        private async Task<TgResult> HandleDisable(long chatId)
        {
            var activeSession = botCache.GetActiveChatSession(chatId);
            if (activeSession != null)
            {
                await botClient.SendMessage(chatId,
                    "Active chat session was ended");
                await botCache.StopChatSession(chatId, db);
            }
            var disabled = await registrationService.DisableTgChatAsync(chatId);
            if (disabled)
            {
                await botClient.SendMessage(chatId,
                    "✅ Your chat has been disabled. You will not recieve requests to start new chat from Meshtastic devices. Devices and channels added via /add_device and /add_channel will still be able to send messages.\n\n" +
                    "Use /start to re-enable it.");
            }
            else
            {
                await botClient.SendMessage(chatId,
                    "This chat is already disabled. You will not recieve requests to start new chat from Meshtastic devices. Devices and channels added via /add_device and /add_channel will still be able to send messages.\n\n" +
                    "Use /start to re-enable it.");
            }
            return TgResult.Ok;
        }

        private async Task MaybeEndOtherChatSession(long chatId, DeviceOrChannelId id, string username)
        {
            var existingSession = botCache.GetActiveChatSession(chatId);
            if (existingSession != null
                && (existingSession.DeviceId != id.DeviceId
                || existingSession.ChannelId != id.ChannelId))
            {
                await botCache.StopChatSession(chatId, db);

                IRecipient recipient = existingSession.DeviceId != null
                    ? await registrationService.GetDeviceAsync(existingSession.DeviceId.Value)
                    : await registrationService.GetChannelAsync(existingSession.ChannelId.Value);

                if (recipient != null)
                {
                    var gatewayId = botCache.GetRecipientGateway(recipient);
                    var tgChat = await registrationService.GetTgChatByChatIdAsync(chatId);
                    var chatName = tgChat != null ? tgChat.ChatName : $"@{username}";
                    meshtasticService.SendTextMessage(
                         recipient,
                         $"Chat with {chatName} is ended",
                         replyToMessageId: null,
                         relayGatewayId: gatewayId,
                         hopLimit: int.MaxValue);
                }
            }
        }

        private async Task<TgResult> HandleChatDeviceCommand(long userId, long chatId, string arg, string username)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                await botClient.SendMessage(chatId,
                    "Please provide a device ID to start a chat.\n\n" +
                    "Examples:\n" +
                    "• /chat !75bcd15\n" +
                    "• /chat #75bcd15\n" +
                    "• /chat 123456789");
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(arg, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{arg}'. The device ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }

            if (deviceId == _options.MeshtasticNodeId)
            {
                await botClient.SendMessage(chatId,
                    $"You can't start a chat with {_options.MeshtasticNodeNameLong} node. This node is used only for relaying messages between devices and Telegram and doesn't have a user interface to reply to messages. Please use /chat command with the ID of your device or a private channel registered with TMesh to start a chat.");
                return TgResult.Ok;
            }

            var device = await registrationService.GetDeviceAsync(deviceId);
            if (device == null)
            {
                await botClient.SendMessage(chatId,
                    $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} is not known to TMesh. The device needs to broadcast its Node info or send it directly to {_options.MeshtasticNodeNameLong} node using \"Exchange user information\".");
                return TgResult.Ok;
            }

            var activeSessionTgChatId = botCache.GetActiveChatSessionForDevice(deviceId);
            bool chatingWithSomeoneElse = activeSessionTgChatId != null
                && activeSessionTgChatId != chatId;

            if (!chatingWithSomeoneElse
                && await registrationService.IsDeviceApprovedForChatAsync(chatId, deviceId))
            {
                var id = new DeviceOrChannelId { DeviceId = deviceId };
                await MaybeEndOtherChatSession(chatId, id, username);
                await botCache.StartChatSession(chatId, id, db);

                await botClient.SendMessage(chatId,
                    $"✅ Chat with {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}) is now active. You can start sending messages.\n\n" +
                    $"All messages you send will be forwarded only to this device. Use /end_chat to end the session.");
            }
            else
            {
                const int maxRequestCount = 50;
                if (!botCache.TryIncreaseRequestsSentCountByTgUser(userId, maxRequestCount, TimeSpan.FromHours(1)))
                {
                    await botClient.SendMessage(chatId, $"You have reached the maximum number of chat requests ({maxRequestCount}) sent. Please wait at least 1 hour before trying again to start a chat with any device. Chat request aborted.");
                    return TgResult.Ok;
                }

                var request = new ChatRequestCode
                {
                    ChatId = chatId,
                    Code = RegistrationService.GenerateRandomCode()
                };

                botCache.StoreDevicePendingChatRequest_TgToMesh(deviceId, request);

                var tgMsgText = new StringBuilder();
                if (chatingWithSomeoneElse)
                {
                    tgMsgText.AppendLine($"{device.NodeName} is chatting with someone else");
                    tgMsgText.AppendLine();
                }
                tgMsgText.AppendLine($"📤 Chat request sent to {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}).");
                tgMsgText.Append($"Waiting for the device to reply with 6 digit numeric code to approve the chat...");

                var msg = await botClient.SendMessage(chatId, tgMsgText.ToString());

                return new TgResult(new OutgoingTextMessage
                {
                    Recipient = device,
                    TelegramChatId = chatId,
                    TelegramMessageId = msg.MessageId,
                    Text = $"Chat request from @{username?.TrimStart('@')} (Telegram). Reply with code {request.Code} to accept."
                });
            }

            return TgResult.Ok;
        }


        private async Task<TgResult> HandleChatChannelCommand(long userId, long chatId, string arg, string username)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                return await TgRespondWithIncorrectChatChannelCommand(chatId);
            }

            var lastIndexOfColon = arg.LastIndexOf(':');
            if (lastIndexOfColon == -1)
            {
                return await TgRespondWithIncorrectChatChannelCommand(chatId);
            }

            var channelNamePart = arg[..lastIndexOfColon].Trim();
            var channelIdPart = arg[(lastIndexOfColon + 1)..].Trim();
            if (string.IsNullOrEmpty(channelNamePart) || string.IsNullOrEmpty(channelIdPart))
            {
                return await TgRespondWithIncorrectChatChannelCommand(chatId);
            }

            if (!int.TryParse(channelIdPart, out var channelId))
            {
                return await TgRespondWithIncorrectChatChannelCommand(chatId);
            }

            var channel = await registrationService.GetChannelAsync(channelId);
            if (channel == null || !string.Equals(channel.Name, channelNamePart, StringComparison.InvariantCulture/*Case sensitive*/))
            {
                await botClient.SendMessage(chatId,
                    $"There are no channels with ID {channelId} and name {channelNamePart}. The channel needs to be registered first with /add_channel command to get ID.");
                return TgResult.Ok;
            }

            var activeSessionTgChatId = botCache.GetActiveChatSessionForChannel(channelId);
            bool chatingWithSomeoneElse = activeSessionTgChatId != null
                && activeSessionTgChatId != chatId;

            if (!chatingWithSomeoneElse && await registrationService.IsChannelApprovedForChatAsync(chatId, channelId))
            {
                var id = new DeviceOrChannelId { ChannelId = channelId };
                await MaybeEndOtherChatSession(chatId, id, username);
                await botCache.StartChatSession(chatId, id, db);

                await botClient.SendMessage(chatId,
                    $"✅ Chat with {channel.Name} is now active. You can start sending messages.\n\n" +
                    $"All messages you send will be forwarded only to this channel. Use /end_chat to end the session.");
            }
            else
            {
                const int maxRequestCount = 50;
                if (!botCache.TryIncreaseRequestsSentCountByTgUser(userId, maxRequestCount, TimeSpan.FromHours(1)))
                {
                    await botClient.SendMessage(chatId, $"You have reached the maximum number of chat requests ({maxRequestCount}) sent. Please wait at least 1 hour before trying again to start a chat with any device or channel. Chat request aborted.");
                    return TgResult.Ok;
                }

                var request = new ChatRequestCode
                {
                    ChatId = chatId,
                    Code = RegistrationService.GenerateRandomCode()
                };

                botCache.StoreChannelPendingChatRequest_TgToMesh(channelId, request);

                var tgMsgText = new StringBuilder();
                if (chatingWithSomeoneElse)
                {
                    tgMsgText.AppendLine($"{channel.Name} is chatting with someone else");
                    tgMsgText.AppendLine();
                }
                tgMsgText.AppendLine($"📤 Chat request sent to {channel.Name}.");
                tgMsgText.Append($"Waiting for the channel to reply with 6 digit numeric code to approve the chat...");

                var msg = await botClient.SendMessage(chatId, tgMsgText.ToString());

                return new TgResult(new OutgoingTextMessage
                {
                    Recipient = channel,
                    TelegramChatId = chatId,
                    TelegramMessageId = msg.MessageId,
                    Text = $"Chat request from @{username} (Telegram). Reply with code {request.Code} to accept."
                });
            }
            return TgResult.Ok;
        }
        private async Task<TgResult> TgRespondWithIncorrectChatChannelCommand(long chatId)
        {
            await botClient.SendMessage(chatId,
                "Please provide a channel name and channel ID to start a chat. You or someone else should register channel first with /add_channel command to get the ID.\n\n" +
                "Examples:\n" +
                "• /chat_channel MyChannel:1\n" +
                "Where 'MyChannel' name is Meshtastic channel name and '1' is the channel ID you get from TMesh when registering the channel with /add_channel command.");
            return TgResult.Ok;
        }

        private async Task<TgResult> HandleStopChat(long chatId)
        {
            var activeSessions = botCache.GetActiveChatSession(chatId);
            if (activeSessions == null)
            {
                await botClient.SendMessage(chatId, "There is no active chat session to stop.");
                return TgResult.Ok;
            }
            string recipientName;
            IRecipient recipient;
            if (activeSessions.DeviceId.HasValue)
            {
                var device = await registrationService.GetDeviceAsync(activeSessions.DeviceId.Value);
                recipient = device;
                recipientName = device != null ? $"{device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(device.DeviceId)})" : $"device {MeshtasticService.GetMeshtasticNodeHexId(activeSessions.DeviceId.Value)}";
            }
            else if (activeSessions.ChannelId.HasValue)
            {
                var channel = await registrationService.GetChannelAsync(activeSessions.ChannelId.Value);
                recipient = channel;
                recipientName = channel != null ? $"{channel.Name} (ID {channel.Id})" : $"Channel ID {activeSessions.ChannelId.Value}";
            }
            else
            {
                recipient = null;
                recipientName = "Unknown";
            }

            await botCache.StopChatSession(chatId, db);
            await botClient.SendMessage(chatId,
                $"✅ Chat session with {recipientName} has been stopped");

            if (recipient != null)
            {
                meshtasticService.SendTextMessage(recipient, $"Chat session with Telegram chat has been stopped", null, null, int.MaxValue);
            }

            return TgResult.Ok;
        }


        private static string GetCmdParam(string cmdFullText)
        {
            var firstSpaceIndex = cmdFullText.IndexOf(' ');
            if (firstSpaceIndex == -1)
            {
                return null;
            }
            else
            {
                return cmdFullText[(firstSpaceIndex + 1)..].Trim();
            }
        }

    }
}
