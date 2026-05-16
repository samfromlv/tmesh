using Meshtastic.Protobufs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Analytics.Dto;
using TBot.Analytics.Models;
using TBot.Helpers;
using TBot.Models.MeshMessages;

namespace TBot.Analytics
{
    public class AnalyticsService(AnalyticsDbContext db, RegistrationService registrationService, TimeZoneHelper tz, IMemoryCache cache)
    {
        public async Task RecordEventAsync(DeviceMetric metrics)
        {
            db.DeviceMetrics.Add(metrics);
            await db.SaveChangesAsync();
        }

        public async Task<int> GetStatistics(DateTime fromUtc)
        {
            var from = Instant.FromDateTimeUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc));
            return await db.DeviceMetrics
                .Where(m => m.Timestamp >= from)
                 .CountAsync();
        }

        public async Task<int> GetStatisticsByNetwork(int networkId, DateTime fromUtc)
        {
            var from = Instant.FromDateTimeUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc));
            return await db.DeviceMetrics
                .Where(m => m.NetworkId == networkId && m.Timestamp >= from)
                .CountAsync();
        }

        public async Task<List<CityDistrict>> GetCityDistrictsAsync(int cityId)
        {
            return await db.CityDistricts
                .Where(d => d.CityId == cityId)
                .ToListAsync();
        }

        public async Task SaveNodeInfo(Packet packet, Models.NodeInfo nodeInfo, PacketBody body)
        {
            packet.Timestamp = Instant.FromDateTimeUtc(DateTime.UtcNow);
            db.Packets.Add(packet);
            await db.SaveChangesAsync();

            nodeInfo.RecordId = packet.RecordId;
            db.NodeInfos.Add(nodeInfo);

            body.RecordId = packet.RecordId;
            db.RawPackets.Add(body);

            await db.SaveChangesAsync();
        }

        public async Task SaveTraceRoute(TraceRouteMessage msg)
        {
            var now = DateTime.UtcNow;
            var localDate = LocalDate.FromDateTime(tz.ConvertFromUtcToDefaultTimezone(now));
            var route = ConvertTraceRouteToPairs(msg);
            var newPairs = new List<TraceRoutePair>();
            newPairs.AddRange(GetPairsFromRoute(msg.NetworkId, msg.IsTowards ? msg.Id : msg.RequestId, route.Route, localDate));
            newPairs.AddRange(GetPairsFromRoute(msg.NetworkId, msg.Id, route.RouteBack, localDate));
            db.TraceRoutePairs.AddRange(newPairs);
            var deviceIds = newPairs.Select(p => p.ToDeviceId).Distinct();
            foreach (var deviceId in deviceIds)
            {
                await MaybeSaveTraceDevice(deviceId, msg.NetworkId, localDate);
            }
            await db.SaveChangesAsync();
        }

        private async ValueTask MaybeSaveTraceDevice(long id, int networkId, LocalDate localToday)
        {
            if (cache.TryGetValue($"TraceDevice_{id}_{localToday:yyyyMMdd}", out _))
            {
                return;
            }
            cache.Set($"TraceDevice_{id}_{localToday:yyyyMMdd}", true, TimeSpan.FromHours(2));

            var device = await registrationService.GetDeviceAsync(id);
            if (device == null)
                return;

            double? latitude = null;
            double? longitude = null;
            if (device.IsLocationPublic)
            {
                latitude = device.Latitude;
                longitude = device.Longitude;
            }

            var updated = await db.TracePairDevices
                .Where(x => x.RecDate == localToday && x.Id == id && x.NetworkId == networkId)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(d => d.Name, device.NodeName)
                    .SetProperty(d => d.Latitude, latitude)
                    .SetProperty(d => d.Longitude, longitude)
                    .SetProperty(d => d.Role, device.Role));

            if (updated == 0)
            {
                db.TracePairDevices.Add(new TracePairDevice
                {
                    Id = id,
                    NetworkId = networkId,
                    RecDate = localToday,
                    Name = device.NodeName,
                    Latitude = latitude,
                    Longitude = longitude,
                    Role = device.Role
                });
            }
        }

        private IEnumerable<TraceRoutePair> GetPairsFromRoute(int networkId, long packetId, List<TraceRoutePairInfo> route, LocalDate localDate)
        {
            for (int i = 0; i < route.Count; i++)
            {
                var pair = route[i];

                if (cache.TryGetValue($"TraceRoutePair_{packetId}_{pair.ToDeviceId}", out _))
                {
                    continue;
                }
                cache.Set($"TraceRoutePair_{packetId}_{pair.ToDeviceId}", true, TimeSpan.FromMinutes(MeshtasticService.LinkTraceExpirationMinutes));

                if (pair.ToDeviceId != MeshtasticService.BroadcastDeviceId
                    && pair.FromDeviceId != MeshtasticService.BroadcastDeviceId)
                {
                    yield return new TraceRoutePair
                    {
                        NetworkId = networkId,
                        ToDeviceId = (uint)pair.ToDeviceId,
                        FromDeviceId = (uint)pair.FromDeviceId,
                        Hops = 0,
                        DirectSnr = pair.Snr,
                        RecDate = localDate
                    };
                }

                for (int j = i - 1; j >= 0; j--)
                {
                    var previousPair = route[j];
                    if (previousPair.FromDeviceId != MeshtasticService.BroadcastDeviceId
                        && pair.ToDeviceId != MeshtasticService.BroadcastDeviceId)
                    {
                        yield return new TraceRoutePair
                        {
                            NetworkId = networkId,
                            ToDeviceId = (uint)pair.ToDeviceId,
                            FromDeviceId = (uint)previousPair.FromDeviceId,
                            Hops = (byte)(i - j),
                            DirectSnr = null,
                            RecDate = localDate
                        };
                    }
                }
            }
        }

        private TraceRouteInfo ConvertTraceRouteToPairs(TraceRouteMessage msg)
        {
            var res = new TraceRouteInfo
            {
                Route = new List<TraceRoutePairInfo>(),
                RouteBack = new List<TraceRoutePairInfo>()
            };

            long from = msg.IsTowards ? msg.DeviceId : msg.ToDeviceId;

            if (msg.RouteDiscovery.Route != null)
            {
                for (int i = 0; i < msg.RouteDiscovery.Route.Count; i++)
                {
                    var to = msg.RouteDiscovery.Route[i];
                    float? snr = msg.RouteDiscovery.SnrTowards != null
                        && msg.RouteDiscovery.SnrTowards.Count > i
                        && msg.RouteDiscovery.SnrTowards[i] != MeshtasticService.TraceRouteSNRDefault
                        ? MeshtasticService.UnroundSnrFromTrace(msg.RouteDiscovery.SnrTowards[i])
                        : null;

                    res.Route.Add(new TraceRoutePairInfo
                    {
                        FromDeviceId = from,
                        ToDeviceId = to,
                        Snr = snr
                    });
                    from = to;
                }
            }

            if (!msg.IsTowards)
            {
                float? snr = msg.RouteDiscovery.SnrTowards != null
                        && msg.RouteDiscovery.SnrTowards.Count == msg.RouteDiscovery.Route.Count - 1
                        && msg.RouteDiscovery.SnrTowards.Last() != MeshtasticService.TraceRouteSNRDefault
                    ? MeshtasticService.UnroundSnrFromTrace(msg.RouteDiscovery.SnrTowards.Last())
                    : null;

                res.Route.Add(new TraceRoutePairInfo
                {
                    FromDeviceId = from,
                    ToDeviceId = msg.DeviceId,
                    Snr = snr
                });

                from = msg.ToDeviceId;


                if (msg.RouteDiscovery.RouteBack != null)
                {
                    for (int i = 0; i < msg.RouteDiscovery.RouteBack.Count; i++)
                    {
                        var to = msg.RouteDiscovery.RouteBack[i];

                        float? snr2 = msg.RouteDiscovery.SnrBack != null
                            && msg.RouteDiscovery.SnrBack.Count > i
                            && msg.RouteDiscovery.SnrBack[i] != MeshtasticService.TraceRouteSNRDefault
                            ? MeshtasticService.UnroundSnrFromTrace(msg.RouteDiscovery.SnrBack[i])
                            : null;

                        res.RouteBack.Add(new TraceRoutePairInfo
                        {
                            FromDeviceId = from,
                            ToDeviceId = to,
                            Snr = snr2
                        });
                        from = to;
                    }

                    if (msg.RouteDiscovery.SnrBack != null)
                    {
                        if (msg.RouteDiscovery.RouteBack.Count == msg.RouteDiscovery.SnrBack.Count - 1)
                        {

                            float? snr2 = msg.RouteDiscovery.SnrBack.Last() != MeshtasticService.TraceRouteSNRDefault
                                ? MeshtasticService.UnroundSnrFromTrace(msg.RouteDiscovery.SnrBack.Last())
                                : null;

                            res.RouteBack.Add(new TraceRoutePairInfo
                            {
                                FromDeviceId = from,
                                ToDeviceId = msg.ToDeviceId,
                                Snr = snr2
                            });
                        }
                    }
                }
            }

            return res;
        }

        public async Task RecordLinkTrace(
            int networkId,
            long packetId,
            long fromGatewayId,
            long toGatewayId,
            byte? step,
            double toLatitude,
            double toLongitude,
            double fromLatitude,
            double fromLongitude)
        {
            var now = DateTime.UtcNow;
            var localNow = tz.ConvertFromUtcToDefaultTimezone(now);
            db.Traces.Add(new LinkTrace
            {
                NetworkId = networkId,
                PacketId = (uint)packetId,
                FromGatewayId = (uint)fromGatewayId,
                ToGatewayId = (uint)toGatewayId,
                Timestamp = Instant.FromDateTimeUtc(now),
                RecDate = LocalDate.FromDateTime(localNow),
                ToLatitude = toLatitude,
                ToLongitude = toLongitude,
                FromLatitude = fromLatitude,
                FromLongitude = fromLongitude,
                Step = step
            });
            await db.SaveChangesAsync();
        }

        public async Task<List<Vote>> GetVotesToProcessAsync()
        {
            var now = Instant.FromDateTimeUtc(DateTime.UtcNow);

            return await db.Votes
                .Where(v => v.IsActive)
                .Include(v => v.Options)
                .ToListAsync();
        }

        public async ValueTask<bool> NetworkHasActiveVotesCached(int networkId)
        {
            return await cache.GetOrCreateAsync($"NetworkHasActiveVotes_{networkId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(60);
                return await db.Votes
                    .AnyAsync(v => v.IsActive && v.NetworkId == networkId);
            });
        }

        public async Task DeactiveVote(int id)
        {
            await db.Votes
                .Where(v => v.Id == id)
                .ExecuteUpdateAsync(v => v.SetProperty(vote => vote.IsActive, false));
        }

        public async Task<List<VoteParticipant>> GetParticipants(int voteId)
        {
            return await db.VoteParticipants
                .Where(p => p.VoteId == voteId)
                .ToListAsync();
        }

        public async Task<List<VoteSnapshotStats>> GetVoteStats(int snapshotId)
        {
            return await db.VoteStats
                .Where(p => p.SnapshotId == snapshotId)
                .ToListAsync();
        }

        public async Task<List<VoteSnapshotStatsByDistrict>> GetVoteStatsByDistrict(int snapshotId)
        {
            return await db.VoteStatsByDistrict
                .Where(p => p.SnapshotId == snapshotId)
                .ToListAsync();
        }

        public async Task Aggregates15MinRefresh()
        {
            await db.Database.ExecuteSqlRawAsync("CALL aggregate_trace_route_pairs();");
        }

        public async Task<List<LatestVoteStat>> GetActiveNetworkVotesLatestStats(int networkId)
        {
            return await (from v in db.Votes
                          join s in db.VoteSnapshots on v.LastSnapshotId equals s.Id
                          join st in db.VoteStats on s.Id equals st.SnapshotId
                          where v.NetworkId == networkId && v.IsActive
                          select new LatestVoteStat
                          {
                              VoteId = v.Id,
                              LastUpdate = v.LastUpdate,
                              ActiveCount = st.ActiveCount,
                              OptionId = st.OptionId
                          }).ToListAsync();
        }

        public void AddRange<T>(IEnumerable<T> rows)
        {
            db.AddRange(rows.Cast<object>());
        }

        public void RemoveRange<T>(IEnumerable<T> rows)
        {
            db.RemoveRange(rows.Cast<object>());
        }

        public void Add<T>(T row)
        {
            db.Add(row);
        }


        public async Task SaveChanges()
        {
            await db.SaveChangesAsync();
        }


        public async Task EnsureMigratedAsync()
        {
            await db.Database.MigrateAsync();
        }
    }
}
