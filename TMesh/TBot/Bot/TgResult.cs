using TBot.Models;

namespace TBot.Bot
{
    public class TgResult
    {
        public static readonly TgResult Ok = new() { Handled = true, MeshMessage = null };
        public static readonly TgResult NotHandled = new() { Handled = false, MeshMessage = null };

        public TgResult()
        {
        }

        public TgResult(OutgoingTextMessage meshMessage)
        {
            MeshMessage = meshMessage;
            Handled = true;
        }

        public TgResult(List<int> networkWithUpdatedGateways)
        {
            NetworkWithUpdatedGateways = networkWithUpdatedGateways;
            Handled = true;
        }

        public TgResult(OutgoingTextMessage meshMessage, List<int> networkWithUpdatedGateways)
        {
            MeshMessage = meshMessage;
            NetworkWithUpdatedGateways = networkWithUpdatedGateways;
            Handled = true;
        }

        public bool Handled { get; set; }

        public OutgoingTextMessage MeshMessage { get; set; }

        public List<int> NetworkWithUpdatedGateways { get; set; }

        public bool NetworksUpdated { get; set; }

        public List<int> NetworkWithUpdatedPublicChannels { get; set; }
    }
}
