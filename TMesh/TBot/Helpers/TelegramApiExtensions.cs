using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot.Helpers
{
    public static class TelegramApiExtensions
    {
        private const string MessageIdInvalid = "MESSAGE_ID_INVALID";

        public static bool IsChatGoneError(this ApiRequestException ex)
        {
            return ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest
                    && !string.IsNullOrEmpty(ex.Message)
                    && (ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("group is deactivated", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("group migrated to supergroup", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("bot was kicked", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsMessageCantBeEditedOrDeletedError(this ApiRequestException ex)
        {
            return ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest
                    && !string.IsNullOrEmpty(ex.Message)
                    && (ex.Message.Contains("message can't be deleted", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("message can't be edited", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains(MessageIdInvalid, StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsMessageCantBeReactedError(this ApiRequestException ex)
        {
            return ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest
                    && !string.IsNullOrEmpty(ex.Message)
                    || ex.Message.Contains("message to react not found", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains(MessageIdInvalid, StringComparison.OrdinalIgnoreCase));
        }

        public static async Task<Message> TrySendMessage(
            this ITelegramBotClient botClient,
            RegistrationService regService,
            ILogger logger,
            long chatId,
            string text,
            ReplyParameters replyParameters = null,
            ParseMode parseMode = ParseMode.None)
        {
            try
            {
                return await botClient.SendMessage(
                    chatId,
                    text,
                    replyParameters: replyParameters,
                    parseMode: parseMode);
            }
            catch (ApiRequestException ex) when (ex.IsChatGoneError())
            {
                await regService.RemoveAllForTgChat(chatId);
                return null;
            }
            catch (ApiRequestException ex)
            {
                logger.LogDebug(ex, "Failed to send message to chat {ChatId}", chatId);
                return null;
            }
        }

        public static string GetUserNameOrName(this User user)
        {
            if (user == null) return string.Empty;

            if (!string.IsNullOrEmpty(user.Username))
                return $"@{user.Username.TrimStart('@')}";

            if (!string.IsNullOrEmpty(user.FirstName) && !string.IsNullOrEmpty(user.LastName))
                return $"{user.FirstName} {user.LastName}".Trim();
            return user.FirstName ?? user.LastName ?? $"ID:{user.Id}";

        }
    }
}
