using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.MeshMessages;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static System.Net.Mime.MediaTypeNames;

namespace TBot.Bot
{
    public class TgMessageSender(
        RegistrationService registrationService,
        BotCache botCache,
        MeshtasticBotMsgStatusTracker meshSender,
        TelegramBotClient botClient,
        ILogger<TgMessageSender> logger,
        IOptions<TBotOptions> options)
    {

        private readonly TBotOptions _options = options.Value;

        public async ValueTask AddPublicChannelMeshMessageToTgChats(
            IEnumerable<long> tgChatIds,
            long meshMsgId,
            long deviceId,
            PublicChannel channel,
            string messageText,
            long? replyToMeshMsgId = null,
            MeshtasticMessageStatus status = null)
        {
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }
            string deviceName;
            if (deviceId == _options.MeshtasticNodeId)
            {
                deviceName = _options.MeshtasticNodeNameLong;
            }
            else
            {
                var device = await registrationService.GetDeviceAsync(deviceId);
                deviceName = device != null ? device.NodeName : MeshtasticService.GetMeshtasticNodeHexId(deviceId);
            }
            var colorSymbol = StringHelper.ColorSymbols[HashHelper.ColorIndexFromDeviceId((uint)deviceId, StringHelper.ColorSymbols.Length)];

            MeshtasticMessageStatus replyToStatus = null;

            if (replyToMeshMsgId.HasValue && replyToMeshMsgId.Value != 0)
            {
                replyToStatus = botCache.GetMeshMessageStatus(replyToMeshMsgId.Value);
            }

            var newMsgUids = new List<TgMessageUid>(tgChatIds.Count());

            foreach (var chatId in tgChatIds)
            {
                ReplyParameters replyParameters = null;

                if (replyToStatus != null)
                {
                    var chatMsgUid = replyToStatus.TgMessageUids.FirstOrDefault(x => x.ChatId == chatId);

                    if (chatMsgUid != null)
                    {
                        replyParameters = new ReplyParameters()
                        {
                            AllowSendingWithoutReply = true,
                            ChatId = chatId,
                            MessageId = chatMsgUid.MessageId,
                        };
                    }
                }

                var tgMsg = await TrySendMessage(
                    chatId: chatId,
                    text: $"{colorSymbol} {deviceName}: {messageText}",
                    replyParameters: replyParameters);

                if (tgMsg == null) continue;

                newMsgUids.Add(new TgMessageUid
                {
                    ChatId = chatId,
                    MessageId = tgMsg.Id,
                    IsFromUser = false
                });
            }

            if (newMsgUids.Count == 0)
            {
                return;
            }
            if (status == null)
            {
                status = new MeshtasticMessageStatus
                {
                    TgMessageUids = newMsgUids.ToArray(),
                    MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { meshMsgId, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = channel.Id,
                                        Type = RecipientType.PublicChannel,
                                        Status = DeliveryStatus.Delivered,
                                    }
                                }
                            },
                    BotReplyId = null
                };
                botCache.StoreMeshMessageStatus(channel.NetworkId, meshMsgId, status);
            }
            else
            {
                status.TgMessageUids = status.TgMessageUids.Concat(newMsgUids).ToArray();
            }

            foreach (var msg in newMsgUids)
            {
                meshSender.StoreTelegramMessageStatus(channel.NetworkId, msg.ChatId, msg.MessageId, status);
            }
        }

        public async ValueTask AddPrivateChannelMeshMessageToTgChats(
           IEnumerable<long> tgChatIds,
           long meshMsgId,
           long? deviceId,
           Channel channel,
           string messageText,
           long? replyToMeshMsgId = null,
           MeshtasticMessageStatus status = null)
        {
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            string deviceName = null;
            string colorSymbolPart = "";

            if (deviceId == null)
            {
                deviceName = "Unknown Device";
            }
            else if (deviceId == _options.MeshtasticNodeId)
            {
                deviceName = _options.MeshtasticNodeNameLong;
            }
            else
            {
                var device = await registrationService.GetDeviceAsync(deviceId.Value);
                deviceName = device != null ? device.NodeName : MeshtasticService.GetMeshtasticNodeHexId(deviceId.Value);
            }

            string deviceNamePart = null;
            if (deviceName != null)
            {
                 deviceNamePart = $"{deviceName} [#{channel.Name}]: ";
            }

            if (!channel.IsSingleDevice && deviceId.HasValue)
            {
                var colorSymbol = StringHelper.ColorSymbols[HashHelper.ColorIndexFromDeviceId((uint)deviceId.Value, StringHelper.ColorSymbols.Length)];
                colorSymbolPart = $"{colorSymbol} ";
            }

            MeshtasticMessageStatus replyToStatus = null;

            if (replyToMeshMsgId.HasValue && replyToMeshMsgId.Value != 0)
            {
                replyToStatus = botCache.GetMeshMessageStatus(replyToMeshMsgId.Value);
            }

            var newMsgUids = new List<TgMessageUid>(tgChatIds.Count());

            foreach (var chatId in tgChatIds)
            {
                ReplyParameters replyParameters = null;

                if (replyToStatus != null)
                {
                    var chatMsgUid = replyToStatus.TgMessageUids.FirstOrDefault(x => x.ChatId == chatId);

                    if (chatMsgUid != null)
                    {
                        replyParameters = new ReplyParameters()
                        {
                            AllowSendingWithoutReply = true,
                            ChatId = chatId,
                            MessageId = chatMsgUid.MessageId,
                        };
                    }
                }

                var tgMsg = await TrySendMessage(
                   chatId: chatId,
                   text: $"{colorSymbolPart}{deviceNamePart}{messageText}",
                   replyParameters: replyParameters);

                if (tgMsg == null) continue;

                newMsgUids.Add(new TgMessageUid
                {
                    ChatId = chatId,
                    MessageId = tgMsg.Id,
                    IsFromUser = false
                });
            }

            if (newMsgUids.Count == 0)
            {
                return;
            }
            if (status == null)
            {
                status = new MeshtasticMessageStatus
                {
                    TgMessageUids = newMsgUids.ToArray(),
                    MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { meshMsgId, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = channel.Id,
                                        Type = RecipientType.PublicChannel,
                                        Status = DeliveryStatus.Delivered,
                                    }
                                }
                            },
                    BotReplyId = null
                };
                botCache.StoreMeshMessageStatus(channel.NetworkId, meshMsgId, status);
            }
            else
            {
                status.TgMessageUids = status.TgMessageUids.Concat(newMsgUids).ToArray();
            }

            foreach (var msg in newMsgUids)
            {
                meshSender.StoreTelegramMessageStatus(channel.NetworkId, msg.ChatId, msg.MessageId, status);
            }
        }

        private Task<Message> TrySendMessage(
         long chatId,
         string text,
         ReplyParameters replyParameters = null,
         ParseMode parseMode = ParseMode.None)
        {
            return botClient.TrySendMessage(
                 registrationService,
                 logger,
                 chatId,
                 text,
                 replyParameters,
                 parseMode);
        }
    }
}
