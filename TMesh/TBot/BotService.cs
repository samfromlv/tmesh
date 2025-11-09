using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text.Json;
using System.Text;
using TBot.Models;

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
                    await HandleAddDeviceUpdate(userId, userName, chatId, msg, chatState.Value);
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

        private async Task HandleAddDeviceUpdate(
            long userId,
            string userName,
            long chatId,
            Message message,
            ChatState chatState)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await _botClient.SendMessage(chatId, "Registration cancelled.");
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
                _logger.LogWarning("Unexpected chat state {ChatState} in HandleAddDeviceUpdate", chatState);
            }
        }

        private async Task ProcessNeedDeviceId(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && _meshtasticService.TryParseDeviceId(message.Text, out var deviceId))
            {
                if (await _registrationService.HasRegistrationAsync(chatId, deviceId))
                {
                    await _botClient.SendMessage(chatId, $"Device {deviceId} already registered. Registration cancelled.");
                    _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }

                var codesSent = _registrationService.IncrementDeviceCodesSentRecently(deviceId);
                if (codesSent > RegistrationService.MaxCodeVerificationTries)
                {
                    await _botClient.SendMessage(chatId, $"Device {deviceId} has reached the maximum number of verification codes sent. Please wait at least for 1 hour before trying again adding same device to any chats. Registration cancelled.");
                    _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }

                var code = _registrationService.GenerateRandomCode();
                _registrationService.StorePendingCodeAsync(userId, chatId, deviceId, code, DateTimeOffset.UtcNow.AddMinutes(5));
                await _meshtasticService.SendMeshtasticMessage(deviceId, $"TMesh verification code: {code}");
                await _botClient.SendMessage(chatId, $"Verification code sent to device {deviceId}. Please reply with the code. Code is valid for 5 minutes.");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Adding_NeedCode);
            }
            else
            {
                await _botClient.SendMessage(chatId, "Invalid device id. Please send a valid Meshtastic device id. Device id can be decimal or hex (hex starts from ! or #). Send /stop to cancel.");
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
                    await _botClient.SendMessage(chatId, "Invalid or expired code. Please check the code and try again or restart with /add.");
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

            _logger.LogInformation("Received unsupported update type: {Type}", update.Type);
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
                    "Message is too long to send to Meshtastic device. Please keep it under 230 bytes.",
                    replyParameters: new ReplyParameters
                    {
                        AllowSendingWithoutReply = true,
                        ChatId = chatId,
                        MessageId = msgId,
                    });
                return;
            }


            var registrations = await _registrationService.GetRegistrationsAsync(chatId);
            if (registrations.Count == 0)
            {
                await _botClient.SendMessage(
                    chatId,
                    "No registered devices. You can register new device with /add command. Please remove bot from the group if you don't need it.",
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
                await _meshtasticService.SendMeshtasticMessage(reg.DeviceId, userName, text);
            }

            await _botClient.SetMessageReaction(
                chatId,
                msgId,
                [ReactionEmoji.OkHand]);
        }

        private async Task HandleStatus(long chatId)
        {
            var registrations = await _registrationService.GetRegistrationsAsync(chatId);
            if (registrations.Count == 0)
            {
                await _botClient.SendMessage(chatId, "No registered devices. You can register new device with /add command.");
            }
            else
            {
                var lines = registrations.Select(r => $"• Device ID: {r.DeviceId}, Registered by {r.UserName}");
                var text = "Registered devices:\r\n" + string.Join("\r\n", lines);
                await _botClient.SendMessage(chatId, text);
            }
        }

        private async Task StartAdd(long userId, long chatId)
        {
            await _botClient.SendMessage(chatId, "Please send your Meshtastic device id. Device id can be decimal or hex (hex starts from ! or #).");
            _registrationService.SetChatState(userId, chatId, Models.ChatState.Adding_NeedDeviceId);
        }

        private async Task StartRemove(long userId, long chatId)
        {
            await _botClient.SendMessage(chatId, "Please send your Meshtastic device id. Device id can be decimal or hex (hex starts from ! or #).");
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
            var update = JsonSerializer.Deserialize<Update>(payload);
            _logger.LogDebug("Processing inbound Telegram message: {Payload}", payload);
            return HandleUpdate(update);
        }
    }
}
