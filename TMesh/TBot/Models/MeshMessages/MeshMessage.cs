using Meshtastic.Protobufs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TBot.Database.Models;

namespace TBot.Models.MeshMessages
{
    public abstract class MeshMessage
    {
        public long Id { get; set; }

        public string HexId => MeshtasticService.GetMeshtasticNodeHexId(DeviceId);

        public string MessageTypeStr => MessageType.ToString();

        public int NetworkId { get; set; }
        public abstract MeshMessageType MessageType { get; }
        public long DeviceId { get; set; }
        public long? ChannelId { get; set; }

        public bool IsSingleDeviceChannel { get; set; }

        public int HopLimit { get; set; }
        public int HopStart { get; set; }

        public bool NeedAck { get; set; }

        public bool OkToMqtt { get; set; }
        public bool ViaMqtt { get; set; }

        public long GatewayId { get; set; }

        public string EnvelopeChannelName { get; set; }

        public IRecipient DecodedBy { get; set; }

        public int GetSuggestedReplyHopLimit() => MeshtasticService.GetSuggestedReplyHopLimit(this);
    
        public static T FromEnvelope<T>(ServiceEnvelope env, Data decoded, IRecipient recipient)
            where T : MeshMessage, new()
        {
            return new T
            {
                DeviceId = env.Packet.From,
                OkToMqtt = MeshtasticService.OkToMqtt(decoded),
                ViaMqtt = env.Packet.ViaMqtt,
                EnvelopeChannelName = env.ChannelId,
                ChannelId = recipient?.RecipientPrivateChannelId,
                IsSingleDeviceChannel = recipient?.IsSingleDeviceChannel == true,
                GatewayId = MeshtasticService.PraseDeviceHexId(env.GatewayId),
                NeedAck = env.Packet.WantAck
                             && env.Packet.From != MeshtasticService.BroadcastDeviceId
                             && env.Packet.To != MeshtasticService.BroadcastDeviceId,
                HopLimit = (int)env.Packet.HopLimit,
                HopStart = (int)env.Packet.HopStart,
                Id = env.Packet.Id,
                DecodedBy = recipient
            }; 
        }
    }
}
