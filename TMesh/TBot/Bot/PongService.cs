using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Helpers;
using TBot.Models;

namespace TBot.Bot
{
    public class PongService(
        IOptions<TBotOptions> options,
        BotCache botCache,
        MeshtasticService meshtasticService,
        IServiceProvider serviceProvider)
    {
        private const int MaxDistanceMeters = 100_000;
        private const int FirstPingReplyDelaySec = 10;
        private const int MaxPingStatsTimeSec = 120;
        private const int NextPingReplyDelaySec = 7;
        private readonly TBotOptions _options = options.Value;

        public async ValueTask SchedulePingStats(long deviceId, long gatewayId, long pingMessageId, long pongMsgId, RegistrationService registrationService)
        {
            if (!_options.EnablePingStatsLateReply)
            {
                return;
            }

            var pingInfo = new PingInfo
            {
                DeviceId = deviceId,
                Packets = 1,
                PongMessageId = pongMsgId,
                PingDistanceMeters = 0,
                LastUpdated = DateTime.UtcNow
            };

            botCache.StorePingStatus(pingMessageId, pingInfo, FirstPingReplyDelaySec + 10);
            SchedulePingStatsReply(pingMessageId);

            await AddDistance(pingInfo, gatewayId, registrationService);
        }

        private async Task AddDistance(PingInfo pingInfo, long gatewayId, RegistrationService registrationService)
        {
            var sender = await registrationService.GetDeviceAsync(pingInfo.DeviceId);
            if (sender == null
                || sender.Latitude == null
                || sender.Longitude == null)
            {
                return;
            }
            var gateway = await registrationService.GetDeviceAsync(gatewayId);
            if (gateway == null || !gateway.IsLocationPublic || gateway.Latitude == null || gateway.Longitude == null)
            {
                return;
            }

            var distanceMeters = MeshtasticPositionUtils.DistanceMeters(
                sender.Latitude.Value,
                sender.Longitude.Value,
                gateway.Latitude.Value,
                gateway.Longitude.Value);

            if (distanceMeters > MaxDistanceMeters)
            {
                return;
            }
            lock (pingInfo)
            {
                pingInfo.PingDistanceMeters += (int)distanceMeters;
            }
        }

        public async ValueTask<IServiceScope> TryUpdatePingStats(long pingMsgId, long gatewayId, IServiceScope scope)
        {
            var cachedInfo = botCache.GetPingStatus(pingMsgId);
            if (cachedInfo == null)
            {
                return null;
            }
            lock (cachedInfo)
            {
                cachedInfo.Packets += 1;
                cachedInfo.LastUpdated = DateTime.UtcNow;
            }
            scope ??= serviceProvider.CreateScope();
            var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            await AddDistance(cachedInfo, gatewayId, registrationService);
            return scope;
        }

        private void SchedulePingStatsReply(long pingMsgId)
        {
            Task.Run(async () =>
                {
                    var start = DateTime.UtcNow;
                    await Task.Delay(TimeSpan.FromSeconds(FirstPingReplyDelaySec));
                    while (true)
                    {
                        var elapsedTotal = DateTime.UtcNow - start;
                        var cachedInfo = botCache.GetPingStatus(pingMsgId);
                        if (cachedInfo == null)
                        {
                            return;
                        }

                        var sinceLastUpdateSec = (DateTime.UtcNow - cachedInfo.LastUpdated).TotalSeconds;

                        if (sinceLastUpdateSec > NextPingReplyDelaySec || elapsedTotal.TotalSeconds > MaxPingStatsTimeSec)
                        {
                            using var scope = serviceProvider.CreateScope();
                            var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
                            var device = await registrationService.GetDeviceAsync(cachedInfo.DeviceId);

                            if (device == null || device.PublicKey == null)
                            {
                                return;
                            }

                            var replyText = string.Format(_options.Texts.PingStatsReply ?? "Gateways: {0}. Distance sum: {1} m.", cachedInfo.Packets, cachedInfo.PingDistanceMeters > 0 ? cachedInfo.PingDistanceMeters.ToString() : "?");

                            var replyGateway = botCache.GetDeviceGateway(cachedInfo.DeviceId);
                            botCache.RemovePingStatus(pingMsgId);
                            meshtasticService.SendDirectTextMessage(device.DeviceId,
                                device.NetworkId,
                                device.PublicKey,
                                replyText,
                                cachedInfo.PongMessageId,
                                replyGateway?.GatewayId,
                                replyGateway?.ReplyHopLimit ?? int.MaxValue);

                            return;
                        }

                        await Task.Delay(1);
                    }
                });

        }
    }
}
