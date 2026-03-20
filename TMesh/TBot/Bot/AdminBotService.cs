using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using TBot.Models;
using Telegram.Bot;

namespace TBot.Bot
{
    public class AdminBotService(
        TelegramBotClient botClient,
        IOptions<TBotOptions> options,
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
            else if (chatState == ChatState.Default && segments[0] != "login")
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
                await botClient.SendMessage(chatId, "Not found.");
                return TgResult.Ok;
            }

            //idented
            var json = JsonSerializer.Serialize(device, TgBotService.IdentedOptions);

            var registrations = await registrationService.GetChatsByDeviceIdCached(device.DeviceId);


            await botClient.SendMessage(
                chatId,
                $"Found node:\r\n\r\n" +
                json + "\r\n\r\nRegistrations: " + registrations.Count);

            return TgResult.Ok;
        }

        private async Task<TgResult> ListGateways(long chatId)
        {
            var sb = new StringBuilder();
            var networks = await registrationService.GetNetworksCached();
            var gateways = await registrationService.GetGatewaysCached();
            foreach (var network in networks)
            {
                var networkGateways = gateways.Values.Where(g => g.NetworkId == network.Id).ToList();
                sb.AppendLine($"\"{network.Name}\" network registered gateways:");
                foreach (var gw in networkGateways)
                {
                    var device = await registrationService.GetDeviceAsync(gw.DeviceId);
                    var hexId = MeshtasticService.GetMeshtasticNodeHexId(gw.DeviceId);
                    var lastSeen = gw.LastSeen.HasValue ? gw.LastSeen.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Never";
                    sb.AppendLine($"• {device?.NodeName ?? hexId} ({hexId}), Last seen: {lastSeen}");
                }
                sb.AppendLine();
            }
            sb.AppendLine("Default gateways:");
            foreach (var id in _options.GatewayNodeIds)
            {
                var device = await registrationService.GetDeviceAsync(id);
                var hexId = MeshtasticService.GetMeshtasticNodeHexId(id);
                sb.AppendLine($"• {device?.NodeName ?? hexId} ({hexId})");
            }
            await botClient.SendMessage(chatId, sb.ToString());
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
                await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #).");
                return TgResult.Ok;
            }

            var networkId = segments.Length >= 3 && int.TryParse(segments[2], out var parsedNetworkId)
                ? parsedNetworkId
                : (int?)null;

            if (networkId == null)
            {
                await botClient.SendMessage(chatId, $"Invalid or missing network ID. Please specify a valid integer network ID as the third argument.");
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
            var pwd = registrationService.DeriveMqttPasswordForDevice(parsedNodeId);

            await registrationService.RegisterGatewayAsync(parsedNodeId, networkId.Value);
            await botClient.SendMessage(chatId, $"Added gateway {device?.NodeName ?? hexId}.\r\nMQTT username: {hexId}\r\nMQTT password: {pwd}\r\n\r\nPassword only works with TMesh device firmware.");

            return new TgResult([networkId.Value]);
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
            var channelNameEndIndex = cmd.IndexOf(' ');
            if (channelNameEndIndex == -1)
            {
                await botClient.SendMessage(chatId, "Please specify the channel name and announcement text.");
                return TgResult.Ok;
            }
            var channelName = cmd[..channelNameEndIndex].Trim();
            if (!meshtasticService.IsPublicChannelConfigured(channelName))
            {
                await botClient.SendMessage(chatId, $"Channel '{channelName}' is not configured as a public channel.");
                return TgResult.Ok;
            }
            var announcement = cmd[channelNameEndIndex..].Trim();
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
            meshtasticService.SendPublicTextMessage(
                announcement,
                relayGatewayId: null,
                hopLimit: int.MaxValue,
                publicChannelName: channelName);

            await botClient.SendMessage(chatId, $"Announcement sent to {channelName}.");
            return TgResult.Ok;
        }

        private async Task<TgResult> PublicTextPrimary(long chatId, string noPrefix)
        {
            var announcement = noPrefix["public_text_primary".Length..].Trim();
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
            meshtasticService.SendPublicTextMessage(announcement, relayGatewayId: null, hopLimit: int.MaxValue);
            await botClient.SendMessage(chatId, $"Announcement sent to {_options.MeshtasticPrimaryChannelName}.");
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
