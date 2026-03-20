using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using TBot.Models;
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
        IServiceProvider services)
    {
        private readonly TBotOptions _options = options.Value;


        public async Task<TgResult> HandleCommand(
         long userId,
         long chatId,
         Message message)
        {
            if (message.Text?.StartsWith("/add_device", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractFirstArgFromCommand(message.Text, "/add_device");
                return await StartAddDevice(userId, chatId, deviceIdFromCommand);
            }
            if (message.Text?.StartsWith("/add_channel", StringComparison.OrdinalIgnoreCase) == true)
            {
                var (name, key, mode) = ExtractChannelFromCommand(message.Text, "/add_channel");
                return await StartAddChannel(userId, chatId, name, key, mode);
            }
            if (message.Text?.StartsWith("/remove_device", StringComparison.OrdinalIgnoreCase) == true)
            {
                bool isRemoveFromAll = message.Text.StartsWith("/remove_device_from_all_chats", StringComparison.OrdinalIgnoreCase);
                var deviceIdFromCommand = ExtractFirstArgFromCommand(message.Text, isRemoveFromAll ? "/remove_device_from_all_chats" : "/remove_device");
                return await StartRemoveDevice(userId, chatId, deviceIdFromCommand, isRemoveFromAll);
            }
            if (message.Text?.StartsWith("/remove_channel", StringComparison.OrdinalIgnoreCase) == true)
            {
                bool isRemoveFromAll = message.Text.StartsWith("/remove_channel_from_all_chats", StringComparison.OrdinalIgnoreCase);
                var channelIdFromCommand = ExtractFirstArgFromCommand(message.Text, isRemoveFromAll ? "/remove_channel_from_all_chats" : "/remove_channel");
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
                var deviceIdFromCommand = ExtractFirstArgFromCommand(message.Text, "/promote_to_gateway");
                return await StartPromoteToGateway(userId, chatId, deviceIdFromCommand);
            }
            if (message.Text?.StartsWith("/demote_from_gateway", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractFirstArgFromCommand(message.Text, "/demote_from_gateway");
                return await StartDemoteFromGateway(userId, chatId, deviceIdFromCommand);
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

        private async Task<TgResult> HandleStatus(long chatId, string cmdText)
        {
            var channelRegs = await registrationService.GetChannelNamesByChatId(chatId);
            var devices = await registrationService.GetDeviceNamesByChatId(chatId);
            if (devices.Count == 0
                && channelRegs.Count == 0)
            {
                await botClient.SendMessage(chatId, TgBotService.NoDeviceOrChannelMessage);
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
                }

                if (hasFilter
                    && devices.Count == 0
                    && channelRegs.Count == 0)
                {
                    await botClient.SendMessage(chatId, $"No registered devices or channels matching filter '{filter}'.");
                    return TgResult.Ok;
                }

                var gatewayIdSet = await registrationService.GetGatewaysCached();

                var lines =
                        channelRegs.Select(c => $"• Channel: {c.Name} (ID {c.Id}) {(c.IsSingleDevice ? " [Single Device]" : "")}")
                        .Concat(
                          devices.Select(d =>
                          {
                              var gatewayTag = gatewayIdSet.ContainsKey(d.DeviceId) ? " [Gateway \ud83d\udce1]" : "";
                              return $"• Device: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)}){gatewayTag}, last node info {FormatTimeSpan(now - d.LastNodeInfo)} ago, last position update {(d.LastPositionUpdate != null ? FormatTimeSpan(now - d.LastPositionUpdate.Value) + " ago" : "N/A")}";
                          })
                        );

                var text = $"{(hasFilter ? "Filtered" : "Registered")} channels and devices:\r\n" + string.Join("\r\n", lines);
                await botClient.SendMessage(chatId, text);
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
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Operation canceled.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

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
            var mqttAddress = _options.PublicMqttAddress ?? _options.MqttAddress;
            var mqttTopic = _options.PublicMqttTopic ?? _options.MqttMeshtasticTopicPrefix;
            var flasherAddress = _options.PublicFlasherAddress;

            var instructions = new StringBuilder();
            instructions.AppendLine($"\u2705 Device *{deviceName}* ({hexId}) has been promoted to gateway.");
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
            instructions.AppendLine($"\u2022 *Server address:* `{mqttAddress}`");
            instructions.AppendLine($"\u2022 *Username:* `{mqttUsername}`");
            instructions.AppendLine($"\u2022 *Password:* `{mqttPassword}`");
            instructions.AppendLine($"\u2022 *Root topic:* `{mqttTopic}`");
            instructions.AppendLine($"\u2022 *Encryption enabled:* On \u2705");
            instructions.AppendLine($"\u2022 *JSON output enabled:* Off \u274c");
            instructions.AppendLine($"\u2022 *TLS enabled:* Off \u274c");
            instructions.AppendLine();
            instructions.AppendLine("\u26a0\ufe0f The MQTT password only works with custom TMesh firmware.");
            instructions.AppendLine();
            instructions.AppendLine("Please enable Device telemetry in Meshtastic settings, this will help to monitor network quality.");
            instructions.AppendLine();
            instructions.AppendLine("When the first packet will be received by the TMesh from your device, you will get a notification in this chat.");

            await botClient.SendMessage(chatId, instructions.ToString(), parseMode: ParseMode.Markdown);

            return new TgResult([device.NetworkId]);
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
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Operation canceled.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

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
                $"Device: *{deviceName}* ({hexId})\n\n" +
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
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Operation canceled.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

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

        private async Task<TgResult> ProceedNeedChannelName(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && MeshtasticService.IsValidChannelName(message.Text))
            {
                await SendNeedChannelKeyTgMsg(chatId);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedKey,
                    ChannelName = message.Text
                });
            }
            else
            {
                await botClient.SendMessage(chatId,
                   $"Invalid channel name format: '{message.Text}'. The channel name must be a valid Meshtastic channel name (less than 12 bytes).\n\n" +
                   "Please try again or type /stop to cancell the registration.");
            }
            return TgResult.Ok;
        }

        private async Task<TgResult> ProceedNeedChannelKey(long userId, long chatId, Message message, ChatStateWithData state)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && MeshtasticService.TryParseChannelKey(message.Text, out var key))
            {
                if (string.IsNullOrEmpty(state.ChannelName)
                    || !MeshtasticService.IsValidChannelName(state.ChannelName))
                {
                    await botClient.SendMessage(chatId,
                        $"Registration data is corrupted, channel name is missing in chat state. Registration process aborted. Please try again.");
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                if (meshtasticService.IsPublicChannel(state.ChannelName, key))
                {
                    await botClient.SendMessage(chatId,
                                 $"Adding public, well known channels is not allowed. Registration process aborted. Please try with private channels.");
                    registrationService.SetChatState(userId, chatId, ChatState.Default);
                    return TgResult.Ok;
                }

                return await ProcessChannelForAdd(userId, chatId, state.ChannelName, key, message.Text, isSingleDevice: null);
            }
            else
            {
                await botClient.SendMessage(chatId,
                                $"Invalid channel key format: '{message.Text}'. The channel key must be a valid Meshtastic channel key (base64-encoded, 16 or 32 bytes).\n\n" +
                                "Please try again or type /stop to cancell the registration.");
            }
            return TgResult.Ok;
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

            return await ProcessChannelForAdd(userId, chatId, state.ChannelName, state.ChannelKey, Convert.ToBase64String(state.ChannelKey), isSingleDevice);
        }

        private async Task<TgResult> ProcessChannelForAdd(long userId, long chatId, string channelName, byte[] channelKey, string channelKeyBase64, bool? isSingleDevice)
        {
            var dbChannel = await registrationService.FindChannelAsync(channelName, channelKey);
            if (dbChannel != null && await registrationService.HasChannelRegistrationAsync(chatId, dbChannel.Id))
            {
                await botClient.SendMessage(chatId, $"Channel {channelName} with same key is already registered in this chat. Registration aborted.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            // If channel doesn't exist yet and isSingleDevice not yet decided, ask
            if (dbChannel == null && !isSingleDevice.HasValue)
            {
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedSingleDevice,
                    ChannelName = channelName,
                    ChannelKey = channelKey,
                    IsSingleDevice = null
                });

                await botClient.SendMessage(chatId,
                    "Is this channel used by a single device only?\n\n" +
                    "• Reply *single* — Optimized Next-Hop routing will be used (works only for single device channels)\n" +
                    "• Reply *multiple* — Standard broadcast routing will be used\n\n" +
                    "Type /stop to cancel.",
                    parseMode: ParseMode.Markdown);
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
            registrationService.StoreChannelPendingCodeAsync(userId, chatId, channelName, channelKey, isSingleDevice, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await botClient.SendMessage(chatId,
                $"Verification code sent to channel {channelName}. Please reply with the received code here. The code is valid for 5 minutes.");

            var channel = new ChannelKey
            {
                ChannelXor = MeshtasticService.GenerateChannelHash(channelName, channelKey),
                PreSharedKey = channelKey,
                IsSingleDevice = false
            };

            registrationService.SetChatState(userId, chatId, ChatState.AddingDevice_NeedCode);

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
                    $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has not yet been seen by the MQTT node {_options.MeshtasticNodeNameLong} in the Meshtastic network.\r\n" +
                    $"1. Ensure your primary channel is '{_options.MeshtasticPrimaryChannelName}' and the key is '{_options.MeshtasticPrimaryChannelPskBase64}'.\r\n" +
                    "2. Make sure 'OK to MQTT' is enabled in LoRa settings on your device.\r\n" +
                    $"3. Find node {_options.MeshtasticNodeNameLong} (MQTT) in node list, open it and click on 'Exchange user information'. {_options.MeshtasticNodeNameLong} broadcasts it's node info every {_options.SentTBotNodeInfoEverySeconds} seconds.\r\n\r\n" +
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
            registrationService.StoreDevicePendingCodeAsync(userId, chatId, deviceId, code, DateTimeOffset.UtcNow.AddMinutes(5));

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

        private async Task<TgResult> ProceedDeviceAdd(
           long userId,
           long chatId,
           Message message,
           ChatStateWithData chatState)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Registration canceled.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            if (chatState.State == ChatState.AddingDevice_NeedId)
            {
                return await ProceedNeedDeviceId(userId, chatId, message);
            }
            else if (chatState.State == ChatState.AddingDevice_NeedCode)
            {
                return await ProceedNeedCode(userId, chatId, message);
            }
            else
            {
                logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
                return TgResult.Ok;
            }
        }

        private async Task<TgResult> ProceedDeviceRemove(long userId, long chatId, Message message, bool isRemoveFromAll)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Removal canceled.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

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
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Removal canceled.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

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


        public async Task<TgResult> ProcessCommandChat(Message msg, ChatStateWithData chatStateWithData)
        {
            var chatId = msg.Chat.Id;
            var userId = msg.From.Id;

            switch (chatStateWithData?.State)
            {
                case ChatState.AddingDevice_NeedId:
                case ChatState.AddingDevice_NeedCode:
                    return await ProceedDeviceAdd(userId, chatId, msg, chatStateWithData);
                case ChatState.AddingChannel_NeedName:
                case ChatState.AddingChannel_NeedKey:
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
                default:
                    throw new InvalidOperationException($"Unexpected chat state {chatStateWithData} in ProcessCommandChat");
            }
        }

        private async Task<TgResult> ProceedChannelAdd(
           long userId,
           long chatId,
           Message message,
           ChatStateWithData chatState)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Registration canceled.");
                registrationService.SetChatState(userId, chatId, ChatState.Default);
                return TgResult.Ok;
            }

            if (chatState.State == ChatState.AddingChannel_NeedName)
            {
                return await ProceedNeedChannelName(userId, chatId, message);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedKey)
            {
                return await ProceedNeedChannelKey(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedSingleDevice)
            {
                return await ProceedNeedChannelSingleDevice(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedCode)
            {
                return await ProceedNeedCode(userId, chatId, message);
            }
            else
            {
                logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
                return TgResult.Ok;
            }
        }




        private async Task<TgResult> StartAddChannel(long userId, long chatId, string channelNameText, string channelKey, string mode = null)
        {
            if (string.IsNullOrWhiteSpace(channelNameText))
            {
                // No channel name provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic channel name.");
                registrationService.SetChatState(userId, chatId, ChatState.AddingChannel_NeedName);
                return TgResult.Ok;
            }

            // Channel name provided in command, validate it
            if (!MeshtasticService.IsValidChannelName(channelNameText))
            {
                await botClient.SendMessage(chatId,
                     $"Invalid channel name format: '{channelNameText}'. The channel name must be a valid Meshtastic channel name (less than 12 bytes).\n\n" +
                     "Examples:\n" +
                     "• /add_channel MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= single\n" +
                     "• /add_channel MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= multiple\n" +
                     "Or use /add_channel without parameters and I'll ask for the channel name and key.");
                return TgResult.Ok;
            }

            if (string.IsNullOrEmpty(channelKey))
            {
                await SendNeedChannelKeyTgMsg(chatId);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedKey,
                    ChannelName = channelNameText
                });
                return TgResult.Ok;
            }

            if (!MeshtasticService.TryParseChannelKey(channelKey, out var keyBytes))
            {
                await botClient.SendMessage(chatId,
                             $"Invalid channel key format: '{channelKey}'. The channel key must be a valid Meshtastic channel key (base64-encoded, 16 or 32 bytes).\n\n" +
                             "Examples:\n" +
                             "• /add_channel MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= single\n" +
                             "Or use /add_channel without parameters and I'll ask for the channel name and key.");
                return TgResult.Ok;
            }

            if (meshtasticService.IsPublicChannel(channelNameText, keyBytes))
            {
                await botClient.SendMessage(chatId,
                             $"Adding public, well known channels is not allowed.");
                return TgResult.Ok;
            }

            // Parse optional mode argument: single / multiple
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

            return await ProcessChannelForAdd(userId, chatId, channelNameText, keyBytes, channelKey, isSingleDevice);
        }







        private async Task SendNeedChannelKeyTgMsg(long chatId)
        {
            await botClient.SendMessage(chatId, "Please send your Meshtastic channel key (base64-encoded, 16 or 32 bytes).");
        }


        private async Task<TgResult> StartAddDevice(long userId, long chatId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                // No device ID provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
                registrationService.SetChatState(userId, chatId, ChatState.AddingDevice_NeedId);
                return TgResult.Ok;
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

            // Process the device ID (same logic as ProcessNeedDeviceId)
            return await ProcessDeviceIdForAdd(userId, chatId, deviceId);
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
                if (channelRegs.Count == 0)
                {
                    await botClient.SendMessage(chatId, "No channels are registered in this chat.");
                    return TgResult.Ok;
                }

                var lines = channelRegs.Select(c => $"• {c.Name} (ID {c.Id})");

                var sb = new StringBuilder("Please send the ID of the channel you want to remove.");
                sb.AppendLine();
                sb.AppendLine("Registered channels:");
                lines.ToList().ForEach(l => sb.AppendLine(l));

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



        private string ExtractFirstArgFromCommand(string commandText, string command)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return null;
            }

            // Remove the command part and trim
            var text = commandText[command.Length..].Trim();
            if (text == $"@{_options.TelegramBotUserName}")
            {
                return null;
            }

            // Return null if empty (no argument provided)
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Extract first word (argument should not have spaces)
            var parts = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }

        private (string name, string key, string mode) ExtractChannelFromCommand(string commandText, string command)
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
                return (parts[0], null, null);
            }
            else if (parts.Length == 2)
            {
                return (parts[0], parts[1], null);
            }
            else
            {
                return (parts[0], parts[1], parts[2]);
            }
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
