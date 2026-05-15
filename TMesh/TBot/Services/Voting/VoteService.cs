using NetTopologySuite.Geometries;
using NodaTime;
using System;
using System.Buffers;
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
        const int MinMinutesBetweenVoteUpdates = 10;

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


        public async Task ProcessVotes(DateTime nextUpdate)
        {
            var now = DateTime.UtcNow;
            var instantNow = Instant.FromDateTimeUtc(now);
            var instantNextUpdate = Instant.FromDateTimeUtc(nextUpdate);
            var votes = await analyticsService.GetVotesToProcessAsync();

            if (votes.Count == 0)
                return;

            Dictionary<int, List<CityDistrict>> cityDistrictsLookup = new Dictionary<int, List<CityDistrict>>();


            foreach (var vote in votes.OrderBy(x => x.EndsAt))
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
                else if (
                    vote.LastUpdate.HasValue
                    && vote.LastUpdate.Value
                        > instantNow.Plus(Duration.FromMinutes(
                            -Math.Max(
                                Math.Abs(vote.UpdateIntervalMinutes),
                                MinMinutesBetweenVoteUpdates)))
                    && vote.EndsAt > instantNextUpdate)

                {
                    // Updated recently — skip to avoid excessive processing
                    continue;
                }
                else
                {
                    List<CityDistrict> cityDistricts = null;

                    if (vote.CityId != null)
                    {
                        cityDistricts = cityDistrictsLookup.GetValueOrDefault(vote.CityId.Value);
                        if (cityDistricts == null)
                        {
                            cityDistricts = await analyticsService.GetCityDistrictsAsync(vote.CityId.Value);
                            cityDistrictsLookup[vote.CityId.Value] = cityDistricts;
                        }
                    }

                    var voteUpdateNow = now;

                    if (vote.EndsAt < instantNextUpdate)
                    {
                        var tillVoteEnd = vote.EndsAt - instantNow;

                        if (tillVoteEnd.TotalMilliseconds > 50)
                        {
                            //Wait until near the end of the vote to process it, to ensure we have all the votes in
                            await Task.Delay(tillVoteEnd.ToTimeSpan());
                        }
                        voteUpdateNow = vote.EndsAt.ToDateTimeUtc();
                    }


                    await ProcessVote(vote, voteUpdateNow, cityDistricts);
                }
            }
        }

        private async Task ProcessVote(Vote vote, DateTime utcNow, List<CityDistrict> cityDistricts)
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

            var lastStatsByDistrict = vote.LastSnapshotId.HasValue && vote.CityId.HasValue && cityDistricts != null
                    ? (await analyticsService.GetVoteStatsByDistrict(vote.LastSnapshotId.Value))
                        .ToDictionary(s => (s.CityDistrictId, s.OptionId))
                    : null;

            var newStats = new Dictionary<byte, VoteSnapshotStats>();
            var newStatByDistrict = new Dictionary<(int CityDistrictId, byte OptionId), VoteSnapshotStatsByDistrict>();
            var newSnapshot = new VoteSnapshot
            {
                VoteId = vote.Id,
                Timestamp = instantNow,
                PreviousSnapshotId = vote.LastSnapshotId
            };

            analyticsService.Add(newSnapshot);
            await analyticsService.SaveChanges();

            var newSnapshotRecords = new List<VoteSnapshotRecord>(devices.Count);
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

                int? deviceCityDistrict = null;
                if (device.Latitude != null && device.Longitude != null && device.IsLocationPublic)
                {
                    var location = new Point(device.Longitude.Value, device.Latitude.Value) { SRID = 4326 };
                    deviceCityDistrict = cityDistricts?.FirstOrDefault(cd => cd.Borders.Contains(location))?.Id; ;

                    if (deviceCityDistrict != null)
                    {
                        var key = (CityDistrictId: deviceCityDistrict.Value, OptionId: currentVote);
                        if (!newStatByDistrict.TryGetValue(key, out var districtStat))
                        {
                            districtStat = new VoteSnapshotStatsByDistrict
                            {
                                SnapshotId = newSnapshot.Id,
                                CityDistrictId = deviceCityDistrict.Value,
                                OptionId = currentVote,
                                ActiveCount = 0,
                                DeltaFromLastSnapshot = 0
                            };
                            newStatByDistrict[key] = districtStat;
                        }
                        districtStat.ActiveCount++;
                    }
                }

                stat.ActiveCount++;

                var updatedInstant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(device.UpdatedUtc.AddSeconds(1), DateTimeKind.Utc));

                var newSnapshotRecord = new VoteSnapshotRecord
                {
                    SnapshotId = newSnapshot.Id,
                    DeviceId = (uint)device.DeviceId,
                    LongName = deviceName,
                    OptionId = currentVote,
                    LastVote = updatedInstant
                };

                newSnapshotRecords.Add(newSnapshotRecord);

                if (currentVote == NoVote)
                {
                    if (participant == null)
                    {
                        continue;
                    }
                }

                if (participant != null)
                {
                    participant.LongName = deviceName;
                    participant.IsNoVote = currentVote == NoVote;

                    if (updatedInstant > participant.LastVote)
                    {
                        participant.VotePacketId = (uint?)device.LastNodeInfoPacketId;
                        participant.LastVote = updatedInstant;
                        participant.Modified = instantNow;
                        participant.UpdateReason = VoteLog.VoteChangeReason.VoteConfirmed;
                        participant.VoteCount++;
                    }

                    if (participant.CurrentOptionId != currentVote)
                    {
                        participant.Modified = instantNow;
                        participant.PreviousOptionId = participant.CurrentOptionId;
                        participant.CurrentOptionId = currentVote;
                        participant.LastVoteChange = updatedInstant;
                        participant.UpdateReason = VoteLog.VoteChangeReason.VoteChanged;

                        var log = new VoteLog
                        {
                            SnapshotId = newSnapshot.Id,
                            VoteId = vote.Id,
                            DeviceId = participant.DeviceId,
                            LogCreated = instantNow,
                            ChangeMade = updatedInstant,
                            MeshPacketId = participant.VotePacketId,
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
                        FirstVote = updatedInstant,
                        LastVote = updatedInstant,
                        Modified = instantNow,
                        CurrentOptionId = currentVote,
                        IsNoVote = currentVote == NoVote,
                        VoteCount = 1,
                        VoteId = vote.Id,
                        VotePacketId = (uint?)device.LastNodeInfoPacketId,
                        UpdateReason = VoteLog.VoteChangeReason.FirstVote,
                        LastVoteChange = updatedInstant,
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
                        ChangeMade = updatedInstant,
                        MeshPacketId = (uint?)device.LastNodeInfoPacketId,
                        NewLongName = deviceName,
                        OldOptionId = NoVote,
                        NewOptionId = currentVote,
                        Reason = VoteLog.VoteChangeReason.FirstVote
                    };

                    newLogs.Add(log);
                }

                if (device.IsLocationPublic)
                {
                    participant.Latitude = device.Latitude;
                    participant.Longitude = device.Longitude;
                }

                participant.CityDistrictId = deviceCityDistrict;
            }

            foreach (var participant in participantLookup.Values.Where(x => !x.IsNoVote))
            {
                participant.IsNoVote = true;
                participant.PreviousOptionId = participant.CurrentOptionId;
                participant.CurrentOptionId = NoVote;
                participant.LastVoteChange = instantNow;
                participant.Modified = instantNow;
                participant.UpdateReason = VoteLog.VoteChangeReason.VoteExpired;

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



            newSnapshot.WinnerDeviceId = SelectVoteGameWinnerAndSetWinnerIndexes(newSnapshotRecords);

            vote.LastUpdate = instantNow;
            vote.LastSnapshotId = newSnapshot.Id;


            // Compute deltas once, after all active-device counts are finalised
            foreach (var stat in newStats.Values)
            {
                var lastCount = lastStats?.GetValueOrDefault(stat.OptionId)?.ActiveCount ?? 0;
                stat.DeltaFromLastSnapshot = stat.ActiveCount - lastCount;
            }

            foreach (var statByDist in newStatByDistrict.Values)
            {
                var lastCount = lastStatsByDistrict?.GetValueOrDefault((statByDist.CityDistrictId, statByDist.OptionId))?.ActiveCount ?? 0;
                statByDist.DeltaFromLastSnapshot = statByDist.ActiveCount - lastCount;
            }

            analyticsService.AddRange(newParticipants);
            analyticsService.AddRange(newStats.Values);
            analyticsService.AddRange(newStatByDistrict.Values);
            analyticsService.AddRange(newSnapshotRecords);
            analyticsService.AddRange(newLogs);

            await analyticsService.SaveChanges();
        }

        private long? SelectVoteGameWinnerAndSetWinnerIndexes(List<VoteSnapshotRecord> allDevices)
        {
            //1) Sort by last vote
            //2) Get sha512 hash or concatinated node is in hex with ! in UTF8
            //3) Divide hash as number by number of participants and get modulo, add 1 to get 1-based index of winner

            var sortedRecordsWithVote = allDevices
                .Where(x => x.OptionId != NoVote)
                .OrderBy(r => r.LastVote)
                .ThenBy(x => x.DeviceId)
                .ToList();

            if (sortedRecordsWithVote.Count == 0)
            {
                return null;
            }

            using var sha512 = System.Security.Cryptography.SHA512.Create();

            foreach (var record in sortedRecordsWithVote)
            {
                var hexId = MeshtasticService.GetMeshtasticNodeHexId(record.DeviceId);
                var hexBytes = System.Text.Encoding.UTF8.GetBytes(hexId);
                sha512.TransformBlock(hexBytes, 0, hexBytes.Length, null, 0);
            }

            sha512.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = sha512.Hash;
            var bignumber = new System.Numerics.BigInteger(hash, isUnsigned: true, isBigEndian: true);
            var winnerIndex = bignumber % sortedRecordsWithVote.Count;
            var winner = sortedRecordsWithVote[(int)winnerIndex];

            return winner.DeviceId;

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
