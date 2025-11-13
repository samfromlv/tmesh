using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TBot.Helpers;
using TBot.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot
{
    public class BotService
    {
        public BotService(
            TelegramBotClient botClient,
            IOptions<TBotOptions> options,
            RegistrationService registrationService,
            MeshtasticService meshtasticService,
            ILogger<BotService> logger)
        {
            _botClient = botClient;
            _options = options.Value;
            _registrationService = registrationService;
            _meshtasticService = meshtasticService;
            _logger = logger;
        }
        private readonly TelegramBotClient _botClient;
        private readonly TBotOptions _options;
        private readonly RegistrationService _registrationService;
        private readonly MeshtasticService _meshtasticService;
        private readonly ILogger<BotService> _logger;

        public async Task InstallWebhook()
        {
            await _botClient.SetWebhook(
                _options.TelegramUpdateWebhookUrl,
                allowedUpdates: [UpdateType.Message],
                secretToken: _options.TelegramWebhookSecret);

            await _botClient.SetMyCommands(new[]
            {
                new BotCommand
                {
                    Command = "add",
                    Description = "Register a Meshtastic device"
                },
                new BotCommand
                {
                    Command = "remove",
                    Description = "Unregister a Meshtastic device"
                },
                new BotCommand
                {
                    Command = "status",
                    Description = "Show list of registered Meshtastic devices"
                }
            });
        }

        public async Task<WebhookInfo> CheckInstall()
        {
            return await _botClient.GetWebhookInfo();
        }

        private async Task HandleUpdate(Update update)
        {
            if (update.Type != UpdateType.Message
                || update.Message == null
                || update.Message.Chat == null
                || update.Message.From == null) return;

            var msg = update.Message;
            var chatId = msg.Chat.Id;
            var userId = msg.From.Id;
            var userName = msg.From.Username ?? $"{msg.From.FirstName} {msg.From.LastName}".Trim();

            var chatState = _registrationService.GetChatState(userId, chatId);

            switch (chatState)
            {
                case ChatState.Adding_NeedDeviceId:
                case ChatState.Adding_NeedCode:
                    await HandleDeviceAdd(userId, userName, chatId, msg, chatState.Value);
                    break;
                case ChatState.RemovingDevice:
                    {
                        await _botClient.SendMessage(chatId, "Device removal is not implemented yet.");
                        break;
                    }
                default:
                    {
                        await HandleDefaultUpdate(userId, userName, chatId, msg);
                        break;
                    }
            }
        }

        private async Task HandleDeviceAdd(
            long userId,
            string userName,
            long chatId,
            Message message,
            ChatState chatState)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await _botClient.SendMessage(chatId, "Registration canceled.");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            if (chatState == ChatState.Adding_NeedDeviceId)
            {
                await ProcessNeedDeviceId(userId, chatId, message);
            }
            else if (chatState == ChatState.Adding_NeedCode)
            {
                await ProcessNeedCode(userId, userName, chatId, message);
            }
            else
            {
                _logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
            }
        }

        private async Task ProcessNeedDeviceId(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && _meshtasticService.TryParseDeviceId(message.Text, out var deviceId))
            {
                var device = await _registrationService.GetDeviceAsync(deviceId);
                if (device == null)
                {
                    await _botClient.SendMessage(chatId,
                        $"Device {deviceId} has not yet been seen by the MQTT node {_options.MeshtasticNodeNameLong} in the Meshtastic network.\r\n" +
                        $"1. Ensure your primary channel is '{_options.MeshtasticPrimaryChannelName}' and the key is '{_options.MeshtasticPrimaryChannelPskBase64}'.\r\n" +
                        "2. Make sure 'OK to MQTT' is enabled in LoRa settings on your device.\r\n" +
                        $"3. Wait for your node info to reach {_options.MeshtasticNodeNameLong} or exchange user info with the proxy node '{_options.MeshtasticProxyNodeName}'.\r\n\r\n" +
                        "Registration aborted.");
                    _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }

                if (await _registrationService.HasRegistrationAsync(chatId, deviceId))
                {
                    await _botClient.SendMessage(chatId, $"Device {device.NodeName} ({deviceId}) is already registered in this chat. Registration aborted.");
                    _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }

                var codesSent = _registrationService.IncrementDeviceCodesSentRecently(deviceId);
                if (codesSent > RegistrationService.MaxCodeVerificationTries)
                {
                    await _botClient.SendMessage(chatId, $"Device {device.NodeName} ({deviceId}) has reached the maximum number of verification codes sent. Please wait at least 1 hour before trying again to add the same device to any chats. Registration aborted.");
                    _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }

                var code = _registrationService.GenerateRandomCode();
                _registrationService.StorePendingCodeAsync(userId, chatId, deviceId, code, DateTimeOffset.UtcNow.AddMinutes(5));
                _meshtasticService.SendMeshtasticMessage(deviceId, device.PublicKey, $"TMesh verification code: {code}");
                await _botClient.SendMessage(chatId, $"Verification code sent to device {device.NodeName} ({deviceId}). Please reply with the received code here. The code is valid for 5 minutes.");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Adding_NeedCode);
            }
            else
            {
                await _botClient.SendMessage(chatId, "Invalid device ID. Please send a valid Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
            }
        }

        private async Task ProcessNeedCode(long userId, string userName, long chatId, Message message)
        {
            var maybeCode = message.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(maybeCode)
                  && _registrationService.IsValidCodeFormat(maybeCode))
            {
                if (await _registrationService.TryCreateRegistrationWithCode(
                    userId,
                    userName,
                    chatId,
                    maybeCode))
                {
                    await _botClient.SendMessage(chatId, "Registration successful.");
                    _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }
                else
                {
                    await _botClient.SendMessage(chatId, "Invalid or expired code. Please check it and try again, or cancel with /stop.");
                    return;
                }
            }
            else
            {
                await _botClient.SendMessage(chatId, "Invalid code format. Please send the 6-digit verification code sent to your Meshtastic device. Send /stop to cancel.");
            }
        }

        private async Task HandleDefaultUpdate(
            long userId,
            string userName,
            long chatId,
            Message message)
        {
            if (message.Text?.StartsWith("/add", StringComparison.OrdinalIgnoreCase) == true)
            {
                await StartAdd(userId, chatId);
                return;
            }
            if (message.Text?.StartsWith("/remove", StringComparison.OrdinalIgnoreCase) == true)
            {
                await StartRemove(userId, chatId);
                return;
            }
            if (message.Text?.StartsWith("/status", StringComparison.OrdinalIgnoreCase) == true)
            {
                await HandleStatus(chatId);
                return;
            }

            await HandleText(
                message.MessageId,
                userId,
                userName,
                chatId,
                message.Text ?? string.Empty);
        }

        private async Task HandleText(
            int msgId,
            long userId,
            string userName,
            long chatId,
            string text)
        {
            if (!_meshtasticService.CanSendMessage(userName, text))
            {
                await _botClient.SendMessage(
                    chatId,
                    $"Message is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).",
                    replyParameters: new ReplyParameters
                    {
                        AllowSendingWithoutReply = true,
                        ChatId = chatId,
                        MessageId = msgId,
                    });
                return;
            }

            var registrations = await _registrationService.GetDevicesByChatId(chatId);
            if (registrations.Count == 0)
            {
                await _botClient.SendMessage(
                    chatId,
                    "No registered devices. You can register a new device with the /add command. Please remove the bot from the group if you don't need it.",
                    replyParameters: new ReplyParameters
                    {
                        AllowSendingWithoutReply = true,
                        ChatId = chatId,
                        MessageId = msgId,
                    });
                return;
            }

            foreach (var reg in registrations)
            {
                _meshtasticService.SendMeshtasticMessage(reg.DeviceId, reg.PublicKey, userName, text);
            }

            await _botClient.SetMessageReaction(
                chatId,
                msgId,
                [ReactionEmoji.OkHand]);
        }

        private async Task HandleStatus(long chatId)
        {
            var devices = await _registrationService.GetDevicesByChatId(chatId);
            if (devices.Count == 0)
            {
                await _botClient.SendMessage(chatId, "No registered devices. You can register a new device with the /add command.");
            }
            else
            {
                var lines = devices.Select(d => $"• Device: {d.NodeName} ({d.DeviceId}), registered by: {d.RegisteredByUser}");
                var text = "Registered devices:\r\n" + string.Join("\r\n", lines);
                await _botClient.SendMessage(chatId, text);
            }
        }

        private async Task StartAdd(long userId, long chatId)
        {
            await _botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
            _registrationService.SetChatState(userId, chatId, Models.ChatState.Adding_NeedDeviceId);
        }

        private async Task StartRemove(long userId, long chatId)
        {
            await _botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
            _registrationService.SetChatState(userId, chatId, Models.ChatState.RemovingDevice);
        }

        public static void Register(IServiceCollection services)
        {
            services.AddScoped(s =>
            {
                var options = s.GetRequiredService<IOptions<TBotOptions>>();
                return new TelegramBotClient(options.Value.TelegramApiToken);
            });
            services.AddSingleton<MeshtasticService>();
            services.AddScoped<RegistrationService>();
            services.AddScoped<BotService>();
        }

        public Task ProcessInboundTelegramMessage(string payload)
        {
            var update = JsonSerializer.Deserialize<Update>(payload, JsonBotAPI.Options);
            _logger.LogDebug("Processing inbound Telegram message: {Payload}", payload);
            return HandleUpdate(update);
        }

        public async Task ProcessInboundMeshtasticMessage(MeshMessage message)
        {
            if (message.MessageType == MeshMessageType.NodeInfo)
            {
                var nodeInfo = (NodeInfoMessage)message;
                await _registrationService.SetDeviceAsync(
                    message.DeviceId,
                    nodeInfo.NodeName,
                    nodeInfo.PublicKey);
                return;
            }
            else if (message.MessageType == MeshMessageType.Text)
            {
                var device = await _registrationService.GetDeviceAsync(message.DeviceId);
                if (device == null)
                {
                    _logger.LogWarning("Received text message from unknown device {DeviceId}", message.DeviceId);
                    return;
                }

                _logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);
                var registrations = await _registrationService.GetRegistrationsByDeviceId(message.DeviceId);

                if (registrations.Count == 0)
                {
                    _meshtasticService.SendMeshtasticMessage(
                        message.DeviceId,
                        device.PublicKey,
                        $"{StringHelper.Truncate(device.NodeName, 20)} is not registered in @TMesh_bot (Telegram)");
                    return;
                }

                var text = ((TextMessage)message).Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogWarning("Received empty text message from device {DeviceId}", message.DeviceId);
                    return;
                }

                foreach (var reg in registrations)
                {
                    await _botClient.SendMessage(
                        reg.ChatId,
                        $"{reg.UserName} ({device.NodeName}): {text}");
                }

                _meshtasticService.AckMeshtasticMessage(device.PublicKey, message);
            }
        }
    }
}
