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
            if (message.Text?.StartsWith(Commands.Start, StringComparison.OrdinalIgnoreCase) == true
                && (message.Text.Length == 6 || message.Text[6] == ' ' || message.Text[6] == '@'))
            {
                return await HandleStart(userId, chatId, message);
            }
            if (message.Text?.StartsWith(Commands.Kill, StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandleKill(userId, chatId, message);
            }
            if (message.Text?.StartsWith(Commands.Disable, StringComparison.OrdinalIgnoreCase) == true
                && (message.Text.Length == 8 || message.Text[8] == ' ' || message.Text[8] == '@'))
            {
                return await HandleDisable(chatId);
            }
            if (message.Text?.StartsWith(Commands.EndChat, StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandleStopChat(chatId);
            }
            if (message.Text?.StartsWith(Commands.Chat, StringComparison.OrdinalIgnoreCase) == true
                && (message.Text.Length == 5 || message.Text[5] == ' ' || message.Text[5] == '@'))
            {
                var chatArg = ExtractSingleArgFromCommand(message.Text, Commands.Chat);
                return await HandleChatDeviceCommand(userId, chatId, chatArg, message.From.GetUserNameOrName());
            }
            if (message.Text?.StartsWith(Commands.ChatChannel, StringComparison.OrdinalIgnoreCase) == true
               && (message.Text.Length == 13 || message.Text[13] == ' ' || message.Text[13] == '@'))
            {
                var chatArg = ExtractSingleArgFromCommand(message.Text, Commands.ChatChannel);
                return await HandleChatChannelCommand(userId, chatId, chatArg, message.From?.Username);
            }
            if (message.Text?.StartsWith(Commands.AddDevice, StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractSingleArgFromCommand(message.Text, Commands.AddDevice);
                return await StartAddDevice(userId, chatId, deviceIdFromCommand);
            }
            if (message.Text?.StartsWith(Commands.AddChannel, StringComparison.OrdinalIgnoreCase) == true)
            {
                var (networkId, name, key, mode) = ExtractChannelFromCommand(message.Text, Commands.AddChannel);
                return await StartAddChannel(userId, chatId, networkId, name, key, mode);
            }
            if (message.Text?.StartsWith(Commands.RemoveDevice, StringComparison.OrdinalIgnoreCase) == true)
            {
                bool isRemoveFromAll = message.Text.StartsWith(Commands.RemoveDeviceFromAllChats, StringComparison.OrdinalIgnoreCase);
                var deviceIdFromCommand = ExtractSingleArgFromCommand(message.Text, isRemoveFromAll ? Commands.RemoveDeviceFromAllChats : Commands.RemoveDevice);
                return await StartRemoveDevice(userId, chatId, deviceIdFromCommand, isRemoveFromAll);
            }
            if (message.Text?.StartsWith(Commands.RemoveChannel, StringComparison.OrdinalIgnoreCase) == true)
            {
                bool isRemoveFromAll = message.Text.StartsWith(Commands.RemoveChannelFromAllChats, StringComparison.OrdinalIgnoreCase);
                var channelIdFromCommand = ExtractSingleArgFromCommand(message.Text, isRemoveFromAll ? Commands.RemoveChannelFromAllChats : Commands.RemoveChannel);
                return await StartRemoveChannel(userId, chatId, userId, channelIdFromCommand, isRemoveFromAll);
            }
            if (message.Text?.StartsWith(Commands.Status, StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandleStatus(chatId, message.Text);
            }
            if (message.Text?.StartsWith(Commands.Position, StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandlePosition(chatId, message.Text);
            }
            if (message.Text?.StartsWith(Commands.PromoteToGateway, StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractSingleArgFromCommand(message.Text, Commands.PromoteToGateway);
                return await StartPromoteToGateway(userId, chatId, deviceIdFromCommand);
            }
            if (message.Text?.StartsWith(Commands.DemoteFromGateway, StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractSingleArgFromCommand(message.Text, Commands.DemoteFromGateway);
                return await StartDemoteFromGateway(userId, chatId, deviceIdFromCommand);
            }
            if (message.Text?.StartsWith(Commands.Stop, StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, Strings.NoActiveOperationToStop);
                return TgResult.Ok;
            }
            if (message.Text?.StartsWith(Commands.ListNetworks, StringComparison.OrdinalIgnoreCase) == true)
            {
                return await ListNetworks(chatId);
            }
            if (message.Text?.StartsWith(Commands.Admin, StringComparison.OrdinalIgnoreCase) == true)
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
                await botClient.SendMessage(chatId, Strings.NoNetworksConfigured);
                return TgResult.Ok;
            }

            var gateways = await registrationService.GetGatewaysCached();
            var sb = new StringBuilder();
            sb.AppendLine($"🌐 *{Strings.AvailableNetworks_Md1}*");

            foreach (var network in networks)
            {
                sb.AppendLine();
                var urlPart = !string.IsNullOrEmpty(network.Url) ? $" - [{StringHelper.EscapeMd(network.Url)}]({network.Url})" : string.Empty;
                sb.AppendLine($"📍 *{StringHelper.EscapeMd(network.Name)}* - {Strings.Id_Md1} `{network.Id}` {urlPart}");

                var publicChannels = await registrationService.GetPublicChannelsByNetworkAsync(network.Id);
                if (publicChannels.Count == 0)
                {
                    sb.AppendLine($"  _{Strings.NoPublicChannels_Md1}_");
                }
                else
                {
                    foreach (var ch in publicChannels.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Name))
                    {
                        var primaryMark = ch.IsPrimary ? " ⭐" : "  ";
                        sb.AppendLine($"{primaryMark} *{StringHelper.EscapeMd(ch.Name)}* - {Strings.ChannelKeyShort_Md1}: {StringHelper.EscapeMd(MeshtasticService.PskKeyToBase64(ch.Key))}");
                    }
                }

                var networkGateways = gateways.Values.Where(g => g.NetworkId == network.Id).ToList();
                if (networkGateways.Count > 0)
                {
                    sb.AppendLine($"  📡 _{Strings.Gateways_Md1}:_");
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
            sb.AppendLine($"_{string.Format(Strings.CityNotListed_Md1, StringHelper.EscapeMd(_options.AdminTgContact))}_");

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
                    response.AppendLine(Strings.ChatStatus_ChatActive_Md2);
                    response.AppendLine(string.Format(Strings.ChatStatus_ChatCommand_Md2,
                            StringHelper.EscapeMdV2(Commands.Chat), 
                            StringHelper.EscapeMdV2(registeredChat.ChatName),
                            StringHelper.EscapeMd(_options.MeshtasticNodeNameLong)));
                }
                else
                {
                    response.AppendLine(string.Format(Strings.ChatStatus_ChatDisabled_Md2,
                            StringHelper.EscapeMdV2(registeredChat.ChatName)));
                    response.AppendLine(Strings.ChatStatus_ApprovedCanStillStartChat_Md2);
                    response.AppendLine(string.Format(Strings.ChatStatus_ToReenableUseStart_Md2, 
                            StringHelper.EscapeMdV2(Commands.Start)));
                }
            }
            else
            {
                response.AppendLine(Strings.ChatStatus_ChatNotRegistered_Md2);
                response.AppendLine(string.Format(Strings.ChatStatus_StartToRegister_Md2,
                    StringHelper.EscapeMdV2(Commands.Start)));
            }
            response.AppendLine();

            if (devices.Count == 0
                && channelRegs.Count == 0
                && channelApprovals.Count == 0
                && deviceApprovals.Count == 0
                && chatSession == null)
            {
                response.AppendLine(string.Format(Strings.ChatStatus_NoRegisteredDevicesOrChannels_Md2,
                    StringHelper.EscapeMdV2(Commands.AddDevice),
                    StringHelper.EscapeMdV2(Commands.AddChannel),
                    StringHelper.EscapeMdV2(Commands.Chat)));
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
                    response.AppendLine(string.Format(Strings.ChatStatus_NoDevicesMatchFilter_Md2, StringHelper.EscapeMdV2(filter)));
                    await botClient.SendMessage(chatId, response.ToString().TrimEnd(), parseMode: ParseMode.MarkdownV2);
                    return TgResult.Ok;
                }

                if (chatSession != null)
                {
                    response.AppendLine($"💬 *{Strings.ChatStatus_ActiveChatSession_Md2}*");

                    if (chatSession.DeviceId != null)
                    {
                        var device = await registrationService.GetDeviceAsync(chatSession.DeviceId.Value);
                        var hexId = MeshtasticService.GetMeshtasticNodeHexId(chatSession.DeviceId.Value);
                        var network = networks.GetValueOrDefault(device.NetworkId);
                        var name = device?.NodeName ?? hexId;
                        response.AppendLine($"• {Strings.Device_Md2}: {StringHelper.EscapeMdV2(name)} `{StringHelper.EscapeMdV2(hexId)}` \\({StringHelper.EscapeMdV2(network?.Name ?? Strings.Unknown)}\\)");
                    }
                    else if (chatSession.ChannelId != null)
                    {
                        var channel = await registrationService.GetChannelAsync(chatSession.ChannelId.Value);
                        var networkName = networks.GetValueOrDefault(channel.NetworkId)?.Name ?? Strings.Unknown;
                        response.AppendLine($"• {Strings.Channel_Md2}: {StringHelper.EscapeMdV2(channel.Name)} \\({Strings.Id_Md2} `{channel.Id}`\\), {Strings.Network_Md2}: {StringHelper.EscapeMdV2(networkName)}");
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
                        response.AppendLine(
                            $"*{(hasFilter ? Strings.ChatStatus_FilteredChannelsAndDevices_Md2 : Strings.ChatStatus_RegisteredChannelsAndDevices_Md2)}*");

                        if (channelRegs.Count > 0)
                        {
                            response.AppendLine();
                            response.AppendLine($"*{Strings.ChatStatus_RegisteredChannels_Md2}*");
                            foreach (var c in channelRegs)
                            {
                                var networkName = networks.GetValueOrDefault(c.NetworkId)?.Name ?? Strings.Unknown;
                                var singleTag = c.IsSingleDevice ? $" \\[{Strings.SingleDeviceMode_Md2}\\]" : "";
                                response.AppendLine($"• *{StringHelper.EscapeMdV2(c.Name)}*{singleTag} \\- {Strings.Id_Md2} `{c.Id}`, {Strings.Network_Md2}: {StringHelper.EscapeMdV2(networkName)}");
                            }
                        }

                        if (devices.Count > 0)
                        {
                            response.AppendLine();
                            response.AppendLine($"📟 *{Strings.ChatStatus_RegisteredDevices_Md2}*");
                            foreach (var d in devices)
                            {
                                var isGateway = gatewayIdSet.ContainsKey(d.DeviceId);
                                var gatewayTag = isGateway ? $" 📡 \\[{Strings.Gateway_Md2}\\]" : "";
                                var networkName = networks.GetValueOrDefault(d.NetworkId)?.Name ?? Strings.Unknown;
                                var hexId = MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId);
                                var positionStr = d.LastPositionUpdate != null
                                    ? string.Format(Strings.TimestampAgo_Md2, StringHelper.EscapeMdV2(FormatTimeSpan(now - d.LastPositionUpdate.Value)))
                                    : Strings.NotAvailableShort_Md2;
                                response.AppendLine($"• *{StringHelper.EscapeMdV2(d.NodeName)}*{gatewayTag}  `{StringHelper.EscapeMdV2(hexId)}` · {StringHelper.EscapeMdV2(networkName)} · {Strings.NodeInfo_Md2}: {string.Format(Strings.TimestampAgo_Md2, StringHelper.EscapeMdV2(FormatTimeSpan(now - d.LastNodeInfo)))} · {Strings.Position_Md2}: {positionStr}");
                            }
                        }

                    }

                    if (channelApprovals.Count > 0)
                    {
                        response.AppendLine();
                        response.AppendLine($"✅ *{Strings.ChatStatus_ApprovedChannels_Md2}{(hasFilter ? $" \\({Strings.Filtered_Md2}\\)" : "")}:*");
                        foreach (var c in channelApprovals)
                        {
                            var networkName = networks.GetValueOrDefault(c.NetworkId)?.Name ?? Strings.Unknown;
                            var singleTag = c.IsSingleDevice ? $" \\[{Strings.SingleDeviceMode_Md2}\\]" : "";
                            response.AppendLine($"• *{StringHelper.EscapeMdV2(c.Name)}*{singleTag} \\- {Strings.Id_Md2} `{c.Id}`, {Strings.Network_Md2}: {StringHelper.EscapeMdV2(networkName)}");
                        }
                    }

                    if (deviceApprovals.Count > 0)
                    {
                        response.AppendLine();
                        response.AppendLine($"✅ *{Strings.ChatStatus_ApprovedDevices_Md2}{(hasFilter ? $" \\({Strings.Filtered_Md2}\\)" : "")}:*");
                        foreach (var d in deviceApprovals)
                        {
                            var networkName = networks.GetValueOrDefault(d.NetworkId)?.Name ?? Strings.Unknown;
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
                    await botClient.SendMessage(chatId, string.Format(Strings.NoRegisteredDevices, filter));
                    return TgResult.Ok;
                }

                var unknownPositionMsg = new StringBuilder();
                foreach (var d in devices)
                {
                    if (d.LastPositionUpdate == null)
                    {
                        unknownPositionMsg.AppendLine($"• {Strings.Device}: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)})");
                    }
                    else
                    {
                        await botClient.SendMessage(
                            chatId,
                            $"{Strings.Device}: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)}), {Strings.LastPositionUpdate} {string.Format(Strings.TimestampAgo, FormatTimeSpan(now - d.LastPositionUpdate.Value))}:");

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
                        $"{Strings.DevicesWithoutKnownPosition}:\r\n" +
                        unknownPositionMsg.ToString());
                }
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedPromoteToGateway_NeedFirmwareConfirm(long userId, long chatId, Message message)
        {
            var text = message.Text?.Trim();
            if (!string.Equals(text, Strings.yes, StringComparison.OrdinalIgnoreCase))
            {
                var flasherAddress = _options.PublicFlasherAddress;
                var flasherLine = !string.IsNullOrWhiteSpace(flasherAddress)
                    ? string.Format(Strings.PromoteGateway_FlasherLine_Md1, Strings.FlashFirmwareAt_Md1, flasherAddress)
                    : string.Empty;

                await botClient.SendMessage(chatId,
                    string.Format(Strings.PromoteGateway_RepeatFirmwareConfirm_Md1,
                        flasherLine, 
                        StringHelper.EscapeMd(Commands.Stop)),
                    parseMode: ParseMode.Markdown);
                return TgResult.Ok;
            }

            var chatStateWithData = registrationService.GetChatState(userId, chatId);
            if (chatStateWithData?.DeviceId == null)
            {
                await botClient.SendMessage(chatId, string.Format(Strings.PromoteGateway_SessionLost, Commands.PromoteToGateway));
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
                await botClient.SendMessage(chatId, string.Format(Strings.PromoteGateway_DeviceNotFound, Commands.PromoteToGateway));
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
                await botClient.SendMessage(chatId, Strings.DemoteGateway_NeedId);
                registrationService.SetChatState(userId, chatId, ChatState.DemotingFromGateway);
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.DemoteGateway_InvalidIdFormat, deviceIdText));
                return TgResult.Ok;
            }

            return await ExecuteDemoteFromGateway(chatId, deviceId);
        }

        private async Task<TgResult> ProceedDemoteFromGateway(long userId, long chatId, Message message)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.DemoteGateway_NeedIdOrStop, Commands.Stop));
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(text, out var deviceId))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.DemoteGateway_InvalidIdOrStop, Commands.Stop));
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
                    string.Format(Strings.DemoteGateway_NotRegistered, hexId));
                return TgResult.Ok;
            }

            var device = await registrationService.GetDeviceAsync(deviceId);
            var deviceName = device?.NodeName ?? hexId;

            var removed = await registrationService.UnregisterGatewayAsync(deviceId);
            if (removed)
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.DemoteGateway_Done, deviceName, hexId));
                return new TgResult([device.NetworkId]);
            }
            else
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.DemoteGateway_WasNotGateway, deviceName, hexId));
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
                    string.Format(Strings.PromoteGateway_NotRegistered, hexId, Commands.AddDevice));
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var device = await registrationService.GetDeviceAsync(deviceId);
            var deviceName = device?.NodeName ?? hexId;
            var flasherAddress = _options.PublicFlasherAddress;
            var flasherLine = !string.IsNullOrWhiteSpace(flasherAddress)
                ? string.Format(Strings.PromoteGateway_FirmwareConfirmFlasherLine, flasherAddress)
                : string.Empty;

            registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
            {
                State = ChatState.PromotingToGateway_NeedFirmwareConfirm,
                DeviceId = deviceId
            });

            await botClient.SendMessage(chatId,
                string.Format(Strings.PromoteGateway_FirmwareConfirmPrompt_Md1,
                    StringHelper.EscapeMd(deviceName),
                    hexId,
                    flasherLine,
                    StringHelper.EscapeMd(Commands.Stop)),
                parseMode: ParseMode.Markdown);

            return TgResult.Ok;
        }

        private async Task<TgResult> StartPromoteToGateway(long userId, long chatId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                var flasherAddress = _options.PublicFlasherAddress;
                var firmwareLine = !string.IsNullOrWhiteSpace(flasherAddress)
                    ? string.Format(Strings.PromoteGateway_FirmwareWarning, flasherAddress)
                    : Strings.PromoteGateway_FirmwareWarningNoUrl;

                await botClient.SendMessage(chatId,
                    string.Format(Strings.PromoteGateway_NeedId, firmwareLine));
                registrationService.SetChatState(userId, chatId, ChatState.PromotingToGateway);
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.PromoteGateway_InvalidIdFormat, deviceIdText));
                return TgResult.Ok;
            }

            return await AskFirmwareConfirmation(userId, chatId, deviceId);
        }

        private async Task<TgResult> ProceedPromoteToGateway(long userId, long chatId, Message message)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.PromoteGateway_NeedIdOrStop, Commands.Stop));
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(text, out var deviceId))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.PromoteGateway_InvalidIdOrStop, Commands.Stop));
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
                    await botClient.SendMessage(chatId, Strings.VerificationCode_Success);
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                }
                else
                {
                    await botClient.SendMessage(chatId, string.Format(Strings.VerificationCode_InvalidOrExpired, Commands.Stop));
                }
            }
            else
            {
                await botClient.SendMessage(chatId, string.Format(Strings.VerificationCode_InvalidFormat, Commands.Stop));
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
                await botClient.SendMessage(chatId, string.Format(Strings.AddDevice_InvalidIdFormatShort, StringHelper.EscapeMd(Commands.Stop)));
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedNeedChannelNetwork(long userId, long chatId, Message message, ChatStateWithData state)
        {
            var text = message.Text?.Trim();
            if (!int.TryParse(text, out var networkId))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.AddChannel_NeedNetworkId, Commands.Stop));
                return TgResult.Ok;
            }

            var network = await registrationService.GetNetwork(networkId);
            if (network == null)
            {
                await botClient.SendMessage(chatId, string.Format(Strings.AddChannel_InvalidNetworkId, Commands.Stop));
                return TgResult.Ok;
            }

            // Advance to next step depending on what data we already have
            if (string.IsNullOrEmpty(state.ChannelName))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.AddChannel_NetworkSelected, network.Name));
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
                   string.Format(Strings.AddChannel_InvalidName, message.Text, Commands.Stop));
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
                    await botClient.SendMessage(chatId, Strings.AddChannel_CorruptedState);
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                if (await registrationService.IsPublicChannel(state.NetworkId.Value, state.ChannelName, state.ChannelKey))
                {
                    await botClient.SendMessage(chatId, Strings.AddChannel_PublicKeyNotAllowed);
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
                    string.Format(Strings.AddChannel_InsecureKeyRepeatConfirm_Md1, StringHelper.EscapeMd(Commands.Stop)), ParseMode.Markdown);
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
                    await botClient.SendMessage(chatId, Strings.AddChannel_CorruptedState);
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                if (await registrationService.IsPublicChannel(state.NetworkId.Value, state.ChannelName, key))
                {
                    await botClient.SendMessage(chatId, Strings.AddChannel_PublicKeyNotAllowed);
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                if (!state.InsecureKeyConfirmed && MeshtasticService.IsDefaultKey(key))
                {
                    await botClient.SendMessage(chatId,
                                 string.Format(Strings.AddChannel_DefaultKeyWarning_Md1, StringHelper.EscapeMd(Commands.Stop)), ParseMode.Markdown);
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
                    string.Format(Strings.AddChannel_InvalidKey, message.Text, Commands.Stop));
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
                    string.Format(Strings.ChannelPrivacyConfirm_RepeatPrompt_Md1, StringHelper.EscapeMd(Commands.Stop)),
                    ParseMode.Markdown);
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
                    string.Format(Strings.AddChannel_InvalidSingleDeviceReply_Md1, StringHelper.EscapeMd(Commands.Stop)),
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
                await botClient.SendMessage(chatId, Strings.AddChannel_CorruptedStateNetwork);
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var dbChannel = await registrationService.FindChannelAsync(networkId.Value, channelName, channelKey);
            if (dbChannel != null && await registrationService.HasChannelRegistrationAsync(chatId, dbChannel.Id))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.AddChannel_AlreadyRegistered, channelName));
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
                    string.Format(Strings.AddChannel_NeedSingleDevice_Md1, StringHelper.EscapeMd(Commands.Stop)),
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
                    string.Format(Strings.AddChannel_DefaultKeyWarning_Md1, StringHelper.EscapeMd(Commands.Stop)), ParseMode.Markdown);
                return TgResult.Ok;
            }

            var codesSent = registrationService.IncrementChannelCodesSentRecently(channelName, channelKeyBase64);
            if (codesSent > RegistrationService.MaxCodeVerificationTries)
            {
                await botClient.SendMessage(chatId, string.Format(Strings.AddChannel_TooManyCodesSent, channelName));
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var code = RegistrationService.GenerateRandomCode();
            registrationService.StoreChannelPendingCodeAsync(userId, chatId, channelName, channelKey, networkId.Value, isSingleDevice, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await botClient.SendMessage(chatId,
                string.Format(Strings.AddChannel_VerificationCodeSent, channelName));

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
                Text = string.Format(Strings.VerificationCode_Text, code)
            });
        }

        private async Task<TgResult> ProcessDeviceIdForAdd(long userId, long chatId, long deviceId)
        {
            var device = await registrationService.GetDeviceAsync(deviceId);
            if (device == null)
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.AddDevice_NotSeenByNode,
                        MeshtasticService.GetMeshtasticNodeHexId(deviceId),
                        _options.MeshtasticNodeNameLong,
                        _options.SentTBotNodeInfoEverySeconds / 60,
                        Commands.ListNetworks
                        ));
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            if (await registrationService.HasDeviceRegistrationAsync(chatId, deviceId))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.AddDevice_AlreadyRegistered,
                    device.NodeName, MeshtasticService.GetMeshtasticNodeHexId(deviceId)));
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var codesSent = registrationService.IncrementDeviceCodesSentRecently(deviceId);
            if (codesSent > RegistrationService.MaxCodeVerificationTries)
            {
                await botClient.SendMessage(chatId, string.Format(Strings.AddDevice_TooManyCodesSent,
                    device.NodeName, MeshtasticService.GetMeshtasticNodeHexId(deviceId)));
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            var code = RegistrationService.GenerateRandomCode();
            registrationService.StoreDevicePendingCodeAsync(userId, chatId, deviceId, device.NetworkId, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await botClient.SendMessage(chatId,
                string.Format(Strings.AddDevice_VerificationCodeSent,
                    device.NodeName, MeshtasticService.GetMeshtasticNodeHexId(deviceId)));

            registrationService.SetChatState(userId, chatId, ChatState.AddingDevice_NeedCode);

            return new TgResult(new OutgoingTextMessage
            {
                Recipient = device,
                TelegramChatId = chatId,
                TelegramMessageId = msg.MessageId,
                Text = string.Format(Strings.VerificationCode_Text, code)
            });
        }

        private async Task<TgResult> ProceedKillingChatNeedConfirm(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && message.Text.Trim().Equals("yes", StringComparison.InvariantCultureIgnoreCase))
            {
                await registrationService.RemoveAllForTgChat(chatId);
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                await botClient.SendMessage(chatId, string.Format(Strings.Kill_Done, Commands.Start));
                return TgResult.Ok;
            }
            else
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.Kill_RepeatConfirmPrompt_Md1, 
                        StringHelper.EscapeMd(Commands.Stop)),
                    ParseMode.Markdown);
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
                    string.Format(Strings.PrivacyConfirm_RepeatPrompt_Md1, StringHelper.EscapeMd(Commands.Stop)), ParseMode.Markdown);
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
                    string.Format(Strings.PrivacyConfirm_RepeatPrompt_Md1, StringHelper.EscapeMd(Commands.Stop)), ParseMode.Markdown);
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
                await botClient.SendMessage(chatId, string.Format(Strings.RemoveDevice_NeedIdOrStop, Commands.Stop));
                return TgResult.Ok;
            }
            if (!MeshtasticService.TryParseDeviceId(text, out var deviceId))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.RemoveDevice_InvalidIdOrStop, Commands.Stop));
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
                await botClient.SendMessage(chatId, string.Format(Strings.RemoveChannel_NeedIdOrStop, Commands.Stop));
                return TgResult.Ok;
            }
            if (!int.TryParse(text, out var channelId))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.RemoveChannel_InvalidIdOrStop, Commands.Stop));
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

            if (msg.Text?.StartsWith(Commands.Stop, StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, Strings.OperationCanceled);
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
                await botClient.SendMessage(chatId, Strings.RegisterGroup_NeedValidName);
                return TgResult.Ok;
            }

            bool containsSpaces = name.Contains(' ');
            bool tooLong = name.Length > TBotDbContext.MaxChatNameLength;
            bool startsWithAt = name.StartsWith('@');

            if (containsSpaces || tooLong || startsWithAt)
            {
                var error = new StringBuilder(Strings.RegisterGroup_NameInvalidPrefix);
                if (containsSpaces)
                    error.Append(Strings.RegisterGroup_NameInvalidSpaces);
                if (tooLong)
                    error.Append(string.Format(Strings.RegisterGroup_NameInvalidTooLong, TBotDbContext.MaxChatNameLength));
                if (startsWithAt)
                    error.Append(Strings.RegisterGroup_NameInvalidAt);

                error.Append(Strings.RegisterGroup_NameInvalidSuffix);
                await botClient.SendMessage(chatId, error.ToString());
                return TgResult.Ok;
            }

            var normalized = RegistrationService.NormalizeChatName(name, isPrivate: false);

            var tgChat = await registrationService.GetTgChatByNameAsync(normalized);
            if (tgChat != null && tgChat.ChatId != chatId)
            {
                await botClient.SendMessage(chatId, string.Format(Strings.RegisterGroup_NameTaken, name));
                return TgResult.Ok;
            }

            // Proceed with the registration using the provided name
            tgChat = await registrationService.RegisterTgChatAsync(chatId, name, isPrivate: false);
            registrationService.SetChatState(userId, chatId, ChatState.Default);
            await botClient.SendMessage(chatId, string.Format(Strings.RegisterGroup_Done,
                tgChat.ChatName, 
                tgChat.ChatName,
                _options.MeshtasticNodeNameLong,
                Commands.Chat
                ));
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
                        string.Format(Strings.AddChannel_InvalidMode, mode));
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

                var networksList = new StringBuilder();
                foreach (var n in networks)
                    networksList.AppendLine($"*{StringHelper.EscapeMd(n.Name)}* - ID `{n.Id}`");

                await botClient.SendMessage(chatId,
                    string.Format(Strings.AddChannel_SelectNetwork_Md1,
                        networksList.ToString(),
                        StringHelper.EscapeMd(_options.AdminTgContact),
                        StringHelper.EscapeMd(Commands.Stop)
                        ),
                    parseMode: ParseMode.Markdown);
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
                        string.Format(Strings.AddChannel_InvalidIdFormat
                            ,networkIdText
                            ,Commands.AddChannel
                            ));
                    return TgResult.Ok;
                }

                if (!networks.Any(x => x.Id == networkId))
                {
                    var networksList = new StringBuilder();
                    foreach (var n in networks)
                        networksList.AppendLine($"*{StringHelper.EscapeMd(n.Name)}* - {Strings.Id_Md1} `{n.Id}`");

                    await botClient.SendMessage(chatId,
                        string.Format(Strings.AddChannel_NetworkNotFound_Md1, networkId, networksList.ToString(), StringHelper.EscapeMd(Commands.Stop)),
                        parseMode: ParseMode.Markdown);
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
                await botClient.SendMessage(chatId, Strings.AddChannel_NeedName);
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
                     string.Format(Strings.AddChannel_InvalidChannelName, channelNameText, Commands.AddChannel));
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
                    string.Format(Strings.AddChannel_InvalidKeyFormat, 
                        channelKey,
                        Commands.AddChannel));
                return TgResult.Ok;
            }

            if (await registrationService.IsPublicChannel(networkId, channelNameText, keyBytesForSingle))
            {
                await botClient.SendMessage(chatId, Strings.AddChannel_PublicKeyNotAllowedDirect);
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
            await botClient.SendMessage(chatId, Strings.AddChannel_NeedKey);
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
                    string.Format(Strings.AddDevice_InvalidIdFormat, deviceIdText, Commands.AddDevice));
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
            await botClient.SendMessage(chatId, Strings.AddDevice_NeedId);
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
                await botClient.SendMessage(chatId, Strings.RemoveDevice_NeedId);
                registrationService.SetChatState(userId, chatId,
                    isRemoveFromAll ? ChatState.RemovingDeviceFromAll : ChatState.RemovingDevice);
                return TgResult.Ok;
            }

            // Device ID provided in command, process immediately
            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.RemoveDevice_InvalidIdFormat, deviceIdText, Commands.RemoveDevice));
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
                if (channelRegs.Count == 0 && approvals.Count == 0)
                {
                    await botClient.SendMessage(chatId, Strings.RemoveChannel_NoChanels);
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
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("Approved channels:");
                    var lines = approvals.Select(a => $"• {a.Name} (ID {a.Id})");
                    lines.ToList().ForEach(l => sb.AppendLine(l));
                }
                sb.AppendLine("Please send the ID of the channel you want to remove.");
                await botClient.SendMessage(chatId, sb.ToString());
                registrationService.SetChatState(userId, chatId,
                    isRemoveFromAll ? ChatState.RemovingChannelFromAll : ChatState.RemovingChannel);
                return TgResult.Ok;
            }

            // Channel ID provided in command, process immediately
            if (!int.TryParse(channelIdText, out var channelId))
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.RemoveChannel_InvalidIdFormat,
                        channelIdText, 
                        Commands.RemoveChannel));
                return TgResult.Ok;
            }

            await ExecuteRemoveChannel(chatId, telegramUserId, channelId, isRemoveFromAll);
            return TgResult.Ok;
        }

        private async Task ExecuteRemoveDevice(long chatId, long deviceId, bool isRemoveFromAll)
        {
            if (isRemoveFromAll)
            {
                var removedFromAll = await registrationService.RemoveDeviceFromAllChatsViaOneChatAsync(chatId, deviceId);
                if (!removedFromAll)
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveDevice_NotRegisteredInChatForAll, MeshtasticService.GetMeshtasticNodeHexId(deviceId)));
                else
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveDevice_DoneFromAll, MeshtasticService.GetMeshtasticNodeHexId(deviceId)));
            }
            else
            {
                var removed = await registrationService.RemoveDeviceFromChatAsync(chatId, deviceId);
                if (!removed)
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveDevice_NotRegistered, MeshtasticService.GetMeshtasticNodeHexId(deviceId)));
                else
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveDevice_Done, MeshtasticService.GetMeshtasticNodeHexId(deviceId)));
            }
        }


        private async Task ExecuteRemoveChannel(long chatId, long telegramUserId, int channelId, bool isRemoveFromAll)
        {
            if (isRemoveFromAll)
            {
                var (removedFromCurrentChat, removedFromOtherChats) = await registrationService.RemoveChannelFromAllChatsViaOneChatAsync(chatId, telegramUserId, channelId);
                if (!removedFromCurrentChat)
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveChannel_NotRegisteredInChatForAll, channelId));
                else if (removedFromOtherChats)
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveChannel_DoneFromAllWithOthers, channelId));
                else
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveChannel_Done, channelId));
            }
            else
            {
                var removed = await registrationService.RemoveChannelFromChat(chatId, channelId);
                if (!removed)
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveChannel_NotRegistered, channelId));
                else
                    await botClient.SendMessage(chatId, string.Format(Strings.RemoveChannel_Done, channelId));
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
            await botClient.SendMessage(chatId, string.Format(Strings.Kill_ConfirmPrompt_Md1,
                StringHelper.EscapeMd(Commands.Kill), 
                StringHelper.EscapeMd(Commands.Disable),
                StringHelper.EscapeMd(Commands.Stop)), ParseMode.Markdown);
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
            return string.Format(
                Strings.WelcomeMessage_Md1,
                StringHelper.EscapeMd(privacyDisclaimerMsg),
                Strings.yes,
                Commands.Stop);
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
                        await botClient.SendMessage(chatId,
                            string.Format(Strings.Start_GroupAlreadyActive_Md1,
                                StringHelper.EscapeMd(groupChat.ChatName),
                                StringHelper.EscapeMd(groupChat.ChatName),
                                StringHelper.EscapeMd(_options.MeshtasticNodeNameLong),
                                StringHelper.EscapeMd(Commands.Chat),
                                StringHelper.EscapeMd(Commands.Disable)
                                ),
                            ParseMode.Markdown);
                        return TgResult.Ok;
                    }
                }
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    PrivacyConfirmed = true,
                    State = ChatState.RegisteringChat_NeedName
                });
                await botClient.SendMessage(chatId,
                    string.Format(Strings.Start_GroupNeedName,
                        TBotDbContext.MaxChatNameLength,
                        _options.MeshtasticNodeNameLong,
                        Commands.Chat));
                return TgResult.Ok;
            }

            if (string.IsNullOrEmpty(message.From?.Username))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.Start_NeedUsername, Commands.Start));
                return TgResult.Ok;
            }

            var username = message.From.Username;
            var tgChat = await registrationService.RegisterTgChatAsync(chatId, message.From.Username, isPrivate: true);

            registrationService.SetChatState(userId, chatId, ChatState.Default);

            await botClient.SendMessage(chatId,
                string.Format(Strings.Start_PrivateDone_Md2,
                    StringHelper.EscapeMdV2(tgChat.ChatName),
                    StringHelper.EscapeMdV2(_options.MeshtasticNodeNameLong),
                    StringHelper.EscapeMdV2(Commands.AddDevice),
                    StringHelper.EscapeMdV2(Commands.AddChannel),
                    StringHelper.EscapeMdV2(Commands.Chat),
                    StringHelper.EscapeMdV2(Commands.EndChat),
                    StringHelper.EscapeMdV2(Commands.Disable)
                    ),
                parseMode: ParseMode.MarkdownV2);

            return TgResult.Ok;
        }

        private async Task<TgResult> HandleDisable(long chatId)
        {
            var activeSession = botCache.GetActiveChatSession(chatId);
            if (activeSession != null)
            {
                await botClient.SendMessage(chatId, Strings.Disable_ActiveSessionEnded);
                await botCache.StopChatSession(chatId, db);
            }
            var disabled = await registrationService.DisableTgChatAsync(chatId);
            await botClient.SendMessage(chatId, disabled 
                ? string.Format(Strings.Disable_Done, Commands.Start, Commands.AddDevice, Commands.AddChannel)
                : string.Format(Strings.Disable_AlreadyDisabled, Commands.Start, Commands.AddDevice, Commands.AddChannel));
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
                         string.Format(Strings.StopChat_MeshOtherEndedNotification, chatName),
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
                await botClient.SendMessage(chatId, string.Format(Strings.ChatDevice_NeedArg, Commands.Chat));
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseDeviceId(arg, out var deviceId))
            {
                var approvedDevices = await registrationService.GetDeviceApprovalsByChatId(chatId);
                var registeredDevices = await registrationService.GetDeviceNamesByChatId(chatId);

                var matchingDevices = approvedDevices.Where(d => d.NodeName.Contains(arg, StringComparison.InvariantCultureIgnoreCase))
                    .Select(d => d.DeviceId)
                    .Concat(registeredDevices.Where(d => d.NodeName.Contains(arg, StringComparison.InvariantCultureIgnoreCase))
                    .Select(d => d.DeviceId))
                    .ToList();

                if (matchingDevices.Count == 1)
                {
                    deviceId = matchingDevices[0];
                }
                else if (matchingDevices.Count > 1)
                {
                    await botClient.SendMessage(chatId, string.Format(Strings.ChatDevice_MultipleMatch, arg));
                    return TgResult.Ok;
                }
                else if (approvedDevices.Count == 0 && registeredDevices.Count == 0)
                {
                    await botClient.SendMessage(chatId, string.Format(Strings.ChatDevice_NotValidIdNoDevices, arg));
                    return TgResult.Ok;
                }
                else
                {
                    await botClient.SendMessage(chatId, string.Format(Strings.ChatDevice_NotValidIdNoMatch, arg));
                    return TgResult.Ok;
                }
            }

            if (deviceId == _options.MeshtasticNodeId)
            {
                await botClient.SendMessage(chatId, string.Format(Strings.ChatDevice_IsBotNode
                    ,_options.MeshtasticNodeNameLong
                    ,Commands.Chat));
                return TgResult.Ok;
            }

            var device = await registrationService.GetDeviceAsync(deviceId);
            if (device == null)
            {
                await botClient.SendMessage(chatId,
                    string.Format(Strings.ChatDevice_UnknownDevice,
                        MeshtasticService.GetMeshtasticNodeHexId(deviceId),
                        _options.MeshtasticNodeNameLong));
                return TgResult.Ok;
            }

            var activeSessionTgChatId = botCache.GetActiveChatSessionForDevice(deviceId);
            bool chatingWithSomeoneElse = activeSessionTgChatId != null && activeSessionTgChatId != chatId;

            if (!chatingWithSomeoneElse
                && await registrationService.IsDeviceApprovedForChatAsync(chatId, deviceId))
            {
                var id = new DeviceOrChannelId { DeviceId = deviceId };
                await MaybeEndOtherChatSession(chatId, id, username);
                await botCache.StartChatSession(chatId, id, db);

                await botClient.SendMessage(chatId,
                    string.Format(Strings.ChatDevice_SessionStarted,
                        device.NodeName,
                        MeshtasticService.GetMeshtasticNodeHexId(deviceId),
                        Commands.EndChat));
            }
            else
            {
                const int maxRequestCount = 50;
                if (!botCache.TryIncreaseRequestsSentCountByTgUser(userId, maxRequestCount, TimeSpan.FromHours(1)))
                {
                    await botClient.SendMessage(chatId, string.Format(Strings.ChatDevice_TooManyRequests, maxRequestCount));
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
                    tgMsgText.AppendLine(string.Format(Strings.ChatDevice_ChattingWithOther, device.NodeName));
                    tgMsgText.AppendLine();
                }
                tgMsgText.AppendLine(string.Format(Strings.ChatDevice_RequestSent,
                    device.NodeName, MeshtasticService.GetMeshtasticNodeHexId(deviceId)));

                var msg = await botClient.SendMessage(chatId, tgMsgText.ToString());

                return new TgResult(new OutgoingTextMessage
                {
                    Recipient = device,
                    TelegramChatId = chatId,
                    TelegramMessageId = msg.MessageId,
                    Text = string.Format(Strings.ChatDevice_RequestText, username?.TrimStart('@'), request.Code)
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

            int channelId;
            string channelNamePart;
            var lastIndexOfColon = arg.LastIndexOf(':');
            if (lastIndexOfColon == -1)
            {
                var registeredChannels = await registrationService.GetChannelNamesByChatId(chatId);
                var approvedChannels = await registrationService.GetChannelApprovalsByChatId(chatId);
                var matchingChannels = approvedChannels.Where(c => c.Name.Contains(arg, StringComparison.InvariantCultureIgnoreCase))
                    .Select(c => new { c.Id, c.Name })
                    .Concat(registeredChannels.Where(c => c.Name.Contains(arg, StringComparison.InvariantCultureIgnoreCase))
                    .Select(c => new { c.Id, c.Name }))
                    .ToList();

                if (matchingChannels.Count == 1)
                {
                    channelId = matchingChannels[0].Id;
                    channelNamePart = matchingChannels[0].Name;
                }
                else if (matchingChannels.Count > 1)
                {
                    await botClient.SendMessage(chatId, string.Format(Strings.ChatChannel_MultipleMatch, arg));
                    return TgResult.Ok;
                }
                else
                {
                    return await TgRespondWithIncorrectChatChannelCommand(chatId);
                }
            }
            else
            {
                channelNamePart = arg[..lastIndexOfColon].Trim();
                var channelIdPart = arg[(lastIndexOfColon + 1)..].Trim();
                if (string.IsNullOrEmpty(channelNamePart) || string.IsNullOrEmpty(channelIdPart))
                    return await TgRespondWithIncorrectChatChannelCommand(chatId);
                if (!int.TryParse(channelIdPart, out channelId))
                    return await TgRespondWithIncorrectChatChannelCommand(chatId);
            }

            var channel = await registrationService.GetChannelAsync(channelId);
            if (channel == null || !string.Equals(channel.Name, channelNamePart, StringComparison.InvariantCulture))
            {
                await botClient.SendMessage(chatId, string.Format(Strings.ChatChannel_NotFound, channelId, channelNamePart, Commands.AddChannel));
                return TgResult.Ok;
            }

            var activeSessionTgChatId = botCache.GetActiveChatSessionForChannel(channelId);
            bool chatingWithSomeoneElse = activeSessionTgChatId != null && activeSessionTgChatId != chatId;

            if (!chatingWithSomeoneElse && await registrationService.IsChannelApprovedForChatAsync(chatId, channelId))
            {
                var id = new DeviceOrChannelId { ChannelId = channelId };
                await MaybeEndOtherChatSession(chatId, id, username);
                await botCache.StartChatSession(chatId, id, db);

                await botClient.SendMessage(chatId, string.Format(Strings.ChatChannel_SessionStarted, channel.Name, Commands.EndChat));
            }
            else
            {
                const int maxRequestCount = 50;
                if (!botCache.TryIncreaseRequestsSentCountByTgUser(userId, maxRequestCount, TimeSpan.FromHours(1)))
                {
                    await botClient.SendMessage(chatId, string.Format(Strings.ChatChannel_TooManyRequests, maxRequestCount));
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
                    tgMsgText.AppendLine(string.Format(Strings.ChatChannel_ChattingWithOther, channel.Name));
                    tgMsgText.AppendLine();
                }
                tgMsgText.AppendLine(string.Format(Strings.ChatChannel_RequestSent, channel.Name));

                var msg = await botClient.SendMessage(chatId, tgMsgText.ToString());

                return new TgResult(new OutgoingTextMessage
                {
                    Recipient = channel,
                    TelegramChatId = chatId,
                    TelegramMessageId = msg.MessageId,
                    Text = string.Format(Strings.ChatChannel_RequestText, username, request.Code)
                });
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> TgRespondWithIncorrectChatChannelCommand(long chatId)
        {
            await botClient.SendMessage(chatId, string.Format(Strings.ChatChannel_InvalidCommand, Commands.AddChannel, Commands.ChatChannel));
            return TgResult.Ok;
        }

        private async Task<TgResult> HandleStopChat(long chatId)
        {
            var activeSessions = botCache.GetActiveChatSession(chatId);
            if (activeSessions == null)
            {
                await botClient.SendMessage(chatId, Strings.StopChat_NoActiveSession);
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
                recipientName = Strings.Unknown;
            }

            await botCache.StopChatSession(chatId, db);
            await botClient.SendMessage(chatId, string.Format(Strings.StopChat_Done, recipientName));

            if (recipient != null)
            {
                meshtasticService.SendTextMessage(recipient, Strings.StopChat_MeshNotification, null, null, int.MaxValue);
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
