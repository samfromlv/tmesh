using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Analytics;
using TBot.Analytics.Models;
using TBot.Helpers;

namespace TBot.Services.Voting
{
    public class VoteService(AnalyticsService analyticsService, RegistrationService registrationService)
    {

        const byte NoVote = 0;

        //private async Task CreateTest()
        //{
        //    var vote = new Vote
        //    {
        //        NetworkId = 1,
        //        Name = "2026 Test Vote For Medium Fast",
        //        StartsAt = Instant.FromDateTimeUtc(DateTime.UtcNow.AddMinutes(-30)),
        //        EndsAt = Instant.FromDateTimeUtc(DateTime.UtcNow.AddMinutes(180)),
        //        NodeActiveHoursLimit = 24,
        //        Enabled = true,
        //        Description = "This is a test vote for Medium Fast network.",

        //        Options = new List<VoteOption>
        //        {
        //            new VoteOption { OptionId = 1, Prefix = "[MF]", Name = "MediumFast" },
        //            new VoteOption { OptionId = 2, Prefix = "[LF]", Name = "LongFast" }
        //        }
        //    };
        //    analyticsService.Add(vote);
        //    await analyticsService.SaveChanges();
        //}

        public async Task ProcessVotes()
        {
            var now = DateTime.UtcNow;
            var instantNow = Instant.FromDateTimeUtc(now);
            var votes = await analyticsService.GetVotesToProcessAsync();


            foreach (var vote in votes)
            {
                if (!vote.Enabled || vote.EndsAt < instantNow)
                {
                    await analyticsService.DeactiveVote(vote.Id);
                }
                else if (vote.StartsAt > instantNow)
                {
                    // Not yet started — skip silently, do not deactivate
                    continue;
                }
                else if (vote.LastUpdate.HasValue && vote.LastUpdate.Value > instantNow.Plus(Duration.FromMinutes(-vote.UpdateIntervalMinutes - 10)))
                {
                    // Updated recently — skip to avoid excessive processing
                    continue;
                }
                else
                {
                    await ProcessVote(vote, now);
                }
            }
        }

        private async Task ProcessVote(Vote vote, DateTime utcNow)
        {
            var instantNow = Instant.FromDateTimeUtc(utcNow);

            var activeBorder = utcNow.AddHours(-vote.NodeActiveHoursLimit);

            var devices = await registrationService.GetActiveDevicesNamesForVote(vote.NetworkId, activeBorder);
            var participants = await analyticsService.GetParticipants(vote.Id);
            var participantLookup = participants.ToDictionary(d => d.DeviceId);

            var lastStats = vote.LastSnapshotId.HasValue
                    ? (await analyticsService.GetVoteStats(vote.LastSnapshotId.Value))
                        .ToDictionary(s => s.OptionId)
                    : null;

            var newStats = new Dictionary<byte, VoteSnapshotStats>();
            var newSnapshot = new VoteSnapshot
            {
                VoteId = vote.Id,
                Timestamp = instantNow,
                PreviousSnapshotId = vote.LastSnapshotId
            };

            analyticsService.Add(newSnapshot);
            await analyticsService.SaveChanges();

            var newSnapshotRecords = new List<VoteSnapshotRecord>();
            var newParticipants = new List<VoteParticipant>();
            var newLogs = new List<VoteLog>();

            foreach (var device in devices)
            {
                var participant = participantLookup.GetValueOrDefault((uint)device.DeviceId);
                var deviceName = StringHelper.Truncate(device.NodeName, MeshtasticService.MaxLongNodeNameLengthChars);
                var currentVote = GetVoteOptionForDevice(vote, deviceName);

                var stat = newStats.GetValueOrDefault(currentVote);
                if (stat == null)
                {
                    stat = new VoteSnapshotStats
                    {
                        SnapshotId = newSnapshot.Id,
                        OptionId = currentVote,
                    };
                    newStats[currentVote] = stat;
                }

                stat.ActiveCount++;
                if (currentVote == NoVote)
                {
                    if (participant == null)
                    {
                        continue;
                    }
                }


                var newSnapshotRecord = new VoteSnapshotRecord
                {
                    SnapshotId = newSnapshot.Id,
                    DeviceId = (uint)device.DeviceId,
                    LongName = deviceName,
                    OptionId = currentVote
                };

                newSnapshotRecords.Add(newSnapshotRecord);

                if (participant != null)
                {
                    participant.LongName = deviceName;
                    participant.IsNoVote = currentVote == NoVote;

                    if (device.UpdatedUtc > participant.LastVote.ToDateTimeUtc())
                    {
                        participant.LastVote = instantNow;
                        participant.VoteCount++;
                    }

                    if (participant.CurrentOptionId != currentVote)
                    {
                        participant.PreviousOptionId = participant.CurrentOptionId;
                        participant.CurrentOptionId = currentVote;
                        participant.LastVoteChange = instantNow;

                        var log = new VoteLog
                        {
                            SnapshotId = newSnapshot.Id,
                            VoteId = vote.Id,
                            DeviceId = participant.DeviceId,
                            LogCreated = instantNow,
                            ChangeMade = Instant.FromDateTimeUtc(DateTime.SpecifyKind(device.UpdatedUtc, DateTimeKind.Utc)),
                            MeshPacketId = (uint?)device.LastNodeInfoPacketId,
                            NewLongName = deviceName,
                            OldOptionId = participant.PreviousOptionId,
                            NewOptionId = currentVote,
                            Reason = VoteLog.VoteChangeReason.VoteChanged
                        };

                        newLogs.Add(log);
                    }

                    participantLookup.Remove((uint)device.DeviceId);
                }
                else
                {
                    participant = new VoteParticipant
                    {
                        DeviceId = (uint)device.DeviceId,
                        LongName = deviceName,
                        FirstVote = instantNow,
                        LastVote = instantNow,
                        CurrentOptionId = currentVote,
                        IsNoVote = currentVote == NoVote,
                        VoteCount = 1,
                        VoteId = vote.Id,
                        LastVoteChange = instantNow,
                        NodeRegistered = Instant.FromDateTimeUtc(DateTime.SpecifyKind(device.NodeCreatedUtc, DateTimeKind.Utc)),
                        PreviousOptionId = NoVote
                    };
                    newParticipants.Add(participant);

                    var log = new VoteLog
                    {
                        SnapshotId = newSnapshot.Id,
                        VoteId = vote.Id,
                        DeviceId = participant.DeviceId,
                        LogCreated = instantNow,
                        ChangeMade = Instant.FromDateTimeUtc(DateTime.SpecifyKind(device.UpdatedUtc, DateTimeKind.Utc)),
                        MeshPacketId = (uint?)device.LastNodeInfoPacketId,
                        NewLongName = deviceName,
                        OldOptionId = NoVote,
                        NewOptionId = currentVote,
                        Reason = VoteLog.VoteChangeReason.FirstVote
                    };

                    newLogs.Add(log);
                }
            }

            foreach (var participant in participantLookup.Values.Where(x => !x.IsNoVote))
            {
                participant.IsNoVote = true;
                participant.PreviousOptionId = participant.CurrentOptionId;
                participant.CurrentOptionId = NoVote;
                participant.LastVoteChange = instantNow;

                var log = new VoteLog
                {
                    Reason = VoteLog.VoteChangeReason.VoteExpired,
                    SnapshotId = newSnapshot.Id,
                    VoteId = vote.Id,
                    DeviceId = participant.DeviceId,
                    LogCreated = instantNow,
                    ChangeMade = instantNow,
                    MeshPacketId = null,
                    NewLongName = null,
                    OldOptionId = participant.PreviousOptionId,
                    NewOptionId = NoVote
                };

                newLogs.Add(log);
            }

            vote.LastUpdate = instantNow;
            vote.LastSnapshotId = newSnapshot.Id;

            // Compute deltas once, after all active-device counts are finalised
            foreach (var stat in newStats.Values)
            {
                var lastCount = lastStats?.GetValueOrDefault(stat.OptionId)?.ActiveCount ?? 0;
                stat.DeltaFromLastSnapshot = stat.ActiveCount - lastCount;
            }

            analyticsService.AddRange(newParticipants);
            analyticsService.AddRange(newStats.Values);
            analyticsService.AddRange(newSnapshotRecords);
            analyticsService.AddRange(newLogs);

            await analyticsService.SaveChanges();
        }


        private byte GetVoteOptionForDevice(Vote vote, string longName)
        {
            if (string.IsNullOrWhiteSpace(longName))
                return NoVote;


            foreach (var option in vote.Options)
            {
                if (longName.Trim().StartsWith(option.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return option.OptionId;
                }
            }


            // Add logic to determine the vote option based on the longName
            return NoVote;
        }
    }
}
