using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TBot.Helpers
{
    public class TimeZoneHelper
    {
        public TimeZoneHelper(IOptions<TBotOptions> options, ILogger<TimeZoneHelper> _log)
        {
            if (string.IsNullOrEmpty(options.Value.TimeZone))
            {
                _defaultTimezone = TimeZoneInfo.Utc;
                return;
            }

            try
            {
                _defaultTimezone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
            }
            catch (TimeZoneNotFoundException x)
            {
                _log.LogWarning("Time zone not found - " + options.Value.TimeZone, x);
                _defaultTimezone = TimeZoneInfo.Utc;
            }
        }

        private TimeZoneInfo _defaultTimezone;

        public DateTime ConvertFromDefaultTimezoneToUtc(DateTime defaultTimezoneDate)
        {
            return TimeZoneInfo.ConvertTimeToUtc(defaultTimezoneDate, _defaultTimezone);
        }

        public DateTime? ConvertFromDefaultTimezoneToUtc(DateTime? defaultTimezoneDate)
        {
            return defaultTimezoneDate.HasValue
                ? TimeZoneInfo.ConvertTimeToUtc(defaultTimezoneDate.Value, _defaultTimezone)
                : (DateTime?)null;
        }

        public DateTime ConvertFromUtcToDefaultTimezone(DateTime utcDate)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utcDate, _defaultTimezone);
        }
    }
}
