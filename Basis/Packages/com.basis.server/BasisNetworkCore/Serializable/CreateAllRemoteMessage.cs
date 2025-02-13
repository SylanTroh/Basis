using LiteNetLib.Utils;
using System.Collections.Generic;
public static partial class SerializableBasis
{
    public struct CreateAllRemoteMessage
    {
        public ServerReadyMessage[] serverSidePlayer;
        public void Deserialize(NetDataReader Writer, bool AttemptAdditionalData)
        {
            List<ServerReadyMessage> temp = new List<ServerReadyMessage>();
            while (Writer.AvailableBytes != 0)
            {
                ServerReadyMessage ServerReadyMessage = new ServerReadyMessage();
                ServerReadyMessage.Deserialize(Writer, AttemptAdditionalData);
                temp.Add(ServerReadyMessage);
            }
            serverSidePlayer = temp.ToArray();
        }
        public void Serialize(NetDataWriter Writer, bool AttemptAdditionalData)
        {
            int ServerSidePlayers = serverSidePlayer.Length;
            for (int index = 0; index < ServerSidePlayers; index++)
            {
                serverSidePlayer[index].Serialize(Writer, AttemptAdditionalData);
            }
        }
    }
}
