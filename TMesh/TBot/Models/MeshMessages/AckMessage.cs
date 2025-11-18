namespace TBot.Models.MeshMessages
{
    public class AckMessage: MeshMessage
    {
        override public MeshMessageType MessageType => MeshMessageType.AckMessage;

        public bool Success { get; set; }
        public bool IsPkiEncrypted { get; set; }
        public long AckedMessageId { get; set; }

    }
}
