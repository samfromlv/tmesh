using Microsoft.Extensions.Logging;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot.Helpers
{
    public static class TelegramApiExtensions
    {
        private const string MessageIdInvalid = "MESSAGE_ID_INVALID";
        public const int TelegramMessageMaxLength = 4096;

        public static bool IsChatGoneError(this ApiRequestException ex)
        {
            return (ex.ErrorCode == 400 || ex.ErrorCode == 403)
                    && !string.IsNullOrEmpty(ex.Message)
                    && (ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("group is deactivated", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("group migrated to supergroup", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("bot was kicked", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsMessageCantBeEditedOrDeletedError(this ApiRequestException ex)
        {
            return ex.ErrorCode == 400
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
                    && (!string.IsNullOrEmpty(ex.Message)
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

        /// <summary>
        /// Sends <paramref name="lines"/> as one or more MarkdownV2 messages, splitting at line
        /// boundaries so that no single Telegram message exceeds <see cref="TelegramMessageMaxLength"/>
        /// characters.
        /// </summary>
        public static async Task SendLongMessage(
            this ITelegramBotClient botClient,
            long chatId,
            IReadOnlyList<string> lines,
            ParseMode parseMode = ParseMode.None)
        {
            var current = new StringBuilder();
            foreach (var line in lines)
            {
                var needed = (current.Length > 0 ? 1 : 0) + line.Length;
                if (current.Length > 0 && current.Length + needed > TelegramMessageMaxLength)
                {
                    await botClient.SendMessage(chatId, current.ToString().TrimEnd(), parseMode: parseMode);
                    current.Clear();
                }
                current.AppendLine(line);
            }
            if (current.Length > 0)
                await botClient.SendMessage(chatId, current.ToString().TrimEnd(), parseMode: parseMode);
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
