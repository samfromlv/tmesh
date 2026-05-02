using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Analytics.Models;
using TBot.Helpers;

namespace TBot.Analytics
{
    public class AnalyticsService(AnalyticsDbContext db, TimeZoneHelper tz)
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

        public async Task SaveNodeInfo(Packet packet, NodeInfo nodeInfo, PacketBody body)
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
