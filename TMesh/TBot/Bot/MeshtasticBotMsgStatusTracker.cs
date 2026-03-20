using Meshtastic.Discovery;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Database.Models;
using TBot.Models;
using TBot.Models.MeshMessages;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot.Bot
{
    public class MeshtasticBotMsgStatusTracker(TelegramBotClient botClient,
        BotCache botCache,
        MeshtasticService meshtasticService)
    {
        public List<MeshtasticMessageStatus> TrackedMessages { get; }

        public Task SendAndTrackMeshtasticMessage(
           IRecipient recipient,
           long chatId,
           int tgMessageId,
           string text)
        {
            return SendAndTrackMeshtasticMessages(
                [recipient],
                chatId,
                tgMessageId,
                null,
                text);
        }

        public async Task SendAndTrackMeshtasticMessages(
           IEnumerable<IRecipient> recipients,
           long chatId,
           int tgMessageId,
           int? replyToTelegramMsgId,
           string text)
        {
            var status = new MeshtasticMessageStatus
            {
                TelegramChatId = chatId,
                TelegramMessageId = tgMessageId,
                MeshMessages = [],
                EstimatedSendDate = EstimateSendDelay(recipients.Count())
            };

            var messages = new List<(IRecipient recipient, long messageId, DeviceAndGatewayId)>();
            foreach (var recipient in recipients)
            {
                var newMeshMessageId = MeshtasticService.GetNextMeshtasticMessageId();
                var recStatus = new DeliveryStatusWithRecipientId
                {
                    RecipientId = recipient.RecipientDeviceId ?? recipient.RecipientPrivateChannelId,
                    Type = recipient.RecipientType,
                    Status = DeliveryStatus.Queued
                };
                DeviceAndGatewayId deviceAndGatewayId = null;
                if (recipient.IsSingleDeviceChannel == true)
                {
                    deviceAndGatewayId = botCache.GetSingleDeviceChannelGateway((int)recipient.RecipientPrivateChannelId.Value);
                }
                else if (recStatus.Type == RecipientType.Device)
                {
                    deviceAndGatewayId = botCache.GetDeviceGateway(recStatus.RecipientId.Value);
                }
                status.MeshMessages.Add(newMeshMessageId, recStatus);
                botCache.StoreMeshMessageStatus(newMeshMessageId, status);
                messages.Add((recipient, newMeshMessageId, deviceAndGatewayId));
            }

            StoreTelegramMessageStatus(
                chatId,
                tgMessageId,
                status,
                trackForStatusResolve: recipients.Any(x => x.RecipientDeviceId.HasValue));

            await ReportStatus(status);

            var replyToStatus = replyToTelegramMsgId.HasValue
                ? botCache.GetTelegramMessageStatus(chatId, replyToTelegramMsgId.Value)
                : null;

            foreach (var (recipient, newMeshMessageId, deviceAndGatewayId) in messages)
            {
                long? replyToMeshMessageId = null;

                var replyMsg = replyToStatus?.MeshMessages
                    .FirstOrDefault(kv =>
                        kv.Value.RecipientId != null &&
                        kv.Value.RecipientId == recipient.RecipientId &&
                        kv.Value.Type == RecipientType.Device == (recipient.RecipientType == RecipientType.Device));

                if (replyMsg != null && replyMsg.Value.Key != default)
                {
                    replyToMeshMessageId = replyMsg.Value.Key;
                }

                if (recipient.RecipientDeviceId != null)
                {
                    meshtasticService.SendDirectTextMessage(
                            newMeshMessageId,
                            recipient.RecipientDeviceId.Value,
                            recipient.RecipientKey,
                            text,
                            replyToMeshMessageId,
                            deviceAndGatewayId?.GatewayId,
                            hopLimit: deviceAndGatewayId?.ReplyHopLimit ?? int.MaxValue);
                }
                else
                {
                    meshtasticService.SendPrivateChannelTextMessage(
                            newMeshMessageId,
                            text,
                            replyToMeshMessageId,
                            relayGatewayId: deviceAndGatewayId?.GatewayId,
                            hopLimit: deviceAndGatewayId?.ReplyHopLimit ?? int.MaxValue,
                            new ChannelInternalInfo
                            {
                                Psk = recipient.RecipientKey,
                                Hash = recipient.RecipientChannelXor.Value
                            });
                }
            }
        }

        public async Task UpdateMeshMessageStatus(
      long meshMessageId,
      DeliveryStatus newStatus,
      DeliveryStatus? maxCurrentStatus = null)
        {
            var status = botCache.GetMeshMessageStatus(meshMessageId);
            if (status == null)
            {
                return;
            }
            lock (status)
            {
                if (status.MeshMessages.TryGetValue(meshMessageId, out var msgStatus))
                {
                    if (maxCurrentStatus.HasValue
                        && msgStatus.Status > maxCurrentStatus.Value)
                    {
                        return;
                    }

                    if (msgStatus.Type == RecipientType.Channel
                        && newStatus == DeliveryStatus.SentToMqtt)
                    {
                        msgStatus.Status = DeliveryStatus.SentToMqttNoAckExpected;
                    }
                    else
                    {
                        msgStatus.Status = newStatus;
                    }
                }
            }
            await ReportStatus(status);
        }

        public async Task ResolveMessageStatus(long chatId, int telegramMessageId)
        {
            var status = botCache.GetTelegramMessageStatus(chatId, telegramMessageId);
            if (status != null)
            {
                bool madeChanged = false;
                lock (status)
                {
                    foreach (var msg in status.MeshMessages.Values)
                    {
                        if (msg.Status == DeliveryStatus.SentToMqtt)
                        {
                            msg.Status = DeliveryStatus.Unknown;
                            madeChanged = true;
                        }
                    }
                }
                if (madeChanged)
                {
                    await ReportStatus(status);
                }
            }
        }
        private async Task ReportStatus(MeshtasticMessageStatus status)
        {
            if (status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Delivered)
                || status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Unknown)
                || status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.SentToMqttNoAckExpected)
                || status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Failed))
            {
                var deliveryStatus = status.MeshMessages.First().Value;
                string reactionEmoji = ConvertDeliveryStatusToString(deliveryStatus.Status);

                await botClient.SetMessageReaction(
                          status.TelegramChatId,
                          status.TelegramMessageId,
                          [reactionEmoji]);

                int? deletedReplyId = status.BotReplyId;
                if (deletedReplyId != null)
                {
                    await botClient.DeleteMessage(
                        status.TelegramChatId,
                        deletedReplyId.Value);
                    if (deletedReplyId == status.BotReplyId)
                    {
                        status.BotReplyId = null;
                    }
                }
            }
            else
            {
                var sb = new StringBuilder("Status: ");
                var statusesOrdered = status.MeshMessages.OrderBy(x => x.Value.RecipientId).ThenBy(x => x.Key).ToList();
                foreach (var (messageId, deliveryStatus) in statusesOrdered)
                {
                    sb.Append(ConvertDeliveryStatusToString(deliveryStatus.Status));
                }
                if (statusesOrdered.Any(x => x.Value.Status == DeliveryStatus.Queued)
                    && status.EstimatedSendDate.HasValue)
                {
                    var waitTimeSeconds = Math.Ceiling((status.EstimatedSendDate.Value - DateTime.UtcNow).TotalSeconds);
                    if (waitTimeSeconds >= 2)
                    {
                        sb.Append($". Queue wait: {waitTimeSeconds} seconds");
                    }
                }
                if (status.BotReplyId != null)
                {
                    await botClient.EditMessageText(
                        status.TelegramChatId,
                        status.BotReplyId.Value,
                        sb.ToString());
                }
                else
                {
                    var replyMsg = await botClient.SendMessage(
                           status.TelegramChatId,
                           sb.ToString(),
                           replyParameters: new ReplyParameters
                           {
                               AllowSendingWithoutReply = false,
                               ChatId = status.TelegramChatId,
                               MessageId = status.TelegramMessageId,
                           });

                    status.BotReplyId = replyMsg.MessageId;
                }
            }
        }

        public async Task ProcessAckMessages(List<AckMessage> batch)
        {
            foreach (var item in batch)
            {
                botCache.StoreDeviceGateway(item);
                await UpdateMeshMessageStatus(item.AckedMessageId,
                    item.Success
                    ? DeliveryStatus.Delivered
                    : DeliveryStatus.Failed);
            }
        }

        public async Task ProcessMessageSent(long meshtasticMessageId)
        {
            await UpdateMeshMessageStatus(
                meshtasticMessageId,
                DeliveryStatus.SentToMqtt,
                maxCurrentStatus: DeliveryStatus.Queued);
        }
        public void StoreTelegramMessageStatus(
            long chatId,
            int messageId,
            MeshtasticMessageStatus status,
            bool trackForStatusResolve = false)
        {
            botCache.StoreTelegramMessageStatus(chatId, messageId, status);
            if (trackForStatusResolve)
            {
                TrackedMessages.Add(status);
            }
        }
        private static string ConvertDeliveryStatusToString(DeliveryStatus status)
        {
            return status switch
            {
                DeliveryStatus.Created => ReactionEmoji.WritingHand,
                DeliveryStatus.Queued => ReactionEmoji.Eyes,
                DeliveryStatus.SentToMqtt => ReactionEmoji.Dove,
                DeliveryStatus.SentToMqttNoAckExpected => ReactionEmoji.Dove,
                DeliveryStatus.Unknown => ReactionEmoji.ManShrugging,
                DeliveryStatus.Delivered => ReactionEmoji.OkHand,
                DeliveryStatus.Failed => ReactionEmoji.ThumbsDown,
                _ => ReactionEmoji.ExplodingHead,
            };
        }



        public void SendMeshtasticMessageReactions(
           IEnumerable<IRecipient> recipients,
           long chatId,
           int replyToTelegramMsgId,
           string emojis)
        {
            var telMsgStatus = botCache.GetTelegramMessageStatus(chatId, replyToTelegramMsgId);
            if (telMsgStatus == null || telMsgStatus.MeshMessages == null)
            {
                return;
            }

            foreach (var recipient in recipients)
            {

                var replyMsg = telMsgStatus.MeshMessages
                   .FirstOrDefault(kv =>
                       kv.Value.RecipientId != null &&
                       kv.Value.RecipientId == recipient.RecipientId &&
                       kv.Value.Type == RecipientType.Device == (recipient.RecipientType == RecipientType.Device));

                long? replyToMeshMessageId = replyMsg.Key != default ?
                    replyMsg.Key : null;
                if (replyToMeshMessageId == null)
                {
                    continue;
                }

                var newMeshMessageId = MeshtasticService.GetNextMeshtasticMessageId();

                DeviceAndGatewayId deviceAndGatewayId = null;
                if (recipient.IsSingleDeviceChannel == true)
                {
                    deviceAndGatewayId = botCache.GetSingleDeviceChannelGateway((int)recipient.RecipientPrivateChannelId.Value);
                }
                else if (recipient.RecipientDeviceId != null)
                {
                    deviceAndGatewayId = botCache.GetDeviceGateway(recipient.RecipientDeviceId.Value);
                }

                if (recipient.RecipientDeviceId != null)
                {
                    meshtasticService.SendDirectTextMessage(
                            newMeshMessageId,
                            recipient.RecipientDeviceId.Value,
                            recipient.RecipientKey,
                            emojis,
                            replyToMeshMessageId,
                            deviceAndGatewayId?.GatewayId,
                            hopLimit: deviceAndGatewayId?.ReplyHopLimit ?? int.MaxValue,
                            isEmoji: true);
                }
                else
                {
                    meshtasticService.SendPrivateChannelTextMessage(
                            newMeshMessageId,
                            emojis,
                            replyToMeshMessageId,
                            relayGatewayId: deviceAndGatewayId?.GatewayId,
                            hopLimit: deviceAndGatewayId?.ReplyHopLimit ?? int.MaxValue,
                            new ChannelInternalInfo
                            {
                                Psk = recipient.RecipientKey,
                                Hash = recipient.RecipientChannelXor.Value
                            },
                            isEmoji: true);
                }
            }


        }

        private DateTime EstimateSendDelay(int messageCount)
        {
            var delay = meshtasticService.EstimateDelay(MessagePriority.Normal);
            return DateTime.UtcNow
                .Add(delay)
                .Add(meshtasticService.SingleMessageQueueDelay * (messageCount - 1));
        }



    }
}
