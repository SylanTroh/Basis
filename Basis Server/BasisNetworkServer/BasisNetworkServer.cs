using Basis.Network.Core;
using Basis.Network.Server;
using Basis.Network.Server.Auth;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkCore;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static Basis.Network.Server.Generic.BasisSavedState;
using static SerializableBasis;
public static class BasisNetworkServer
{
    public static EventBasedNetListener listener;
    public static NetManager server;
    public static ConcurrentDictionary<ushort, NetPeer> Peers = new ConcurrentDictionary<ushort, NetPeer>();
    public static Configuration Configuration;
    private static IAuth auth;

    public static void StartServer(Configuration configuration)
    {
        Configuration = configuration;
        BasisServerReductionSystem.Configuration = configuration;
        auth = new PasswordAuth(configuration.Password ?? string.Empty);

        SetupServer(configuration);
        SetupServerEvents(configuration);

        if (configuration.EnableStatistics)
        {
            BasisStatistics.StartWorkerThread(BasisNetworkServer.server);
        }
        BNL.Log("Server Worker Threads Booted");

    }
    #region Server Setup
    private static void SetupServer(Configuration configuration)
    {
        listener = new EventBasedNetListener();
        server = new NetManager(listener)
        {
            AutoRecycle = false,
            UnconnectedMessagesEnabled = false,
            NatPunchEnabled = configuration.NatPunchEnabled,
            AllowPeerAddressChange = configuration.AllowPeerAddressChange,
            BroadcastReceiveEnabled = false,
            UseNativeSockets = configuration.UseNativeSockets,
            ChannelsCount = BasisNetworkCommons.TotalChannels,
            EnableStatistics = configuration.EnableStatistics,
            IPv6Enabled = configuration.IPv6Enabled,
            UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
            PingInterval = configuration.PingInterval,
            DisconnectTimeout = configuration.DisconnectTimeout,
            PacketPoolSize = 2000,
            UnsyncedEvents = true,
            
        };

        StartListening(configuration);
    }

    private static void StartListening(Configuration configuration)
    {
        if (configuration.OverrideAutoDiscoveryOfIpv)
        {
            BNL.Log("Server Wiring up SetPort " + Configuration.SetPort + "IPv6Address " + Configuration.IPv6Address);
            server.Start(Configuration.IPv4Address, Configuration.IPv6Address, Configuration.SetPort);
        }
        else
        {
            BNL.Log("Server Wiring up SetPort " + Configuration.SetPort);
            server.Start(Configuration.SetPort);
        }
    }
    #endregion
    #region Server Events Setup

    private static void SetupServerEvents(Configuration configuration)
    {
        SubscribeServerEvents();
    }

    private static void SubscribeServerEvents()
    {
        listener.ConnectionRequestEvent += OnConnectionRequest;
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnNetworkReceive;
        listener.NetworkErrorEvent += OnNetworkError;
    }

    private static void UnsubscribeServerEvents()
    {
        listener.ConnectionRequestEvent -= OnConnectionRequest;
        listener.PeerDisconnectedEvent -= OnPeerDisconnected;
        listener.NetworkReceiveEvent -= OnNetworkReceive;
        listener.NetworkErrorEvent -= OnNetworkError;
    }

    private static void OnConnectionRequest(ConnectionRequest request)
    {
        Task.Run(() => HandleConnectionRequest(request));
    }

    private static void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        Task.Run(() => HandlePeerDisconnected(peer, info));
    }

    private static void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        Task.Run(() => HandleNetworkReceiveEvent(peer, reader, channel, deliveryMethod));
    }

    private static void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Task.Run(() => HandleNetworkErrorEvent(endPoint, socketError));
    }

    #endregion
    #region Worker Thread

    public static void StopWorker()
    {
        server?.Stop();
        UnsubscribeServerEvents();
    }

    #endregion
    #region Server Events Setup
    private static void HandleNetworkErrorEvent(IPEndPoint endPoint, SocketError socketError)
    {
        BNL.LogError($"Endpoint {endPoint.ToString()} was reported with error {socketError}");
    }
    private static void HandleConnectionRequest(ConnectionRequest request)
    {
        try
        {
            BNL.Log("Processing Connection Request");
            int ServerCount = server.ConnectedPeersCount;

            if (ServerCount >= Configuration.PeerLimit)
            {
                RejectWithReason(request, "Server is full! Rejected.");
                return;
            }

            if (!request.Data.TryGetUShort(out ushort ClientVersion))
            {
                RejectWithReason(request, "Invalid client data.");
                return;
            }

            if (ClientVersion < BasisNetworkVersion.ServerVersion)
            {
                RejectWithReason(request, "Outdated client version.");
                return;
            }

            // Decide if connection should be approved
            {

                AuthenticationMessage authMessage = new AuthenticationMessage();
                authMessage.Deserialize(request.Data);


                if (auth.IsAuthenticated(authMessage) == false)
                {
                    RejectWithReason(request, "Authentication failed, password rejected");
                    return;
                }

                BNL.Log("Player approved. Current count: " + ServerCount);
            }

            // Finalize connection
            {

                NetPeer newPeer = request.Accept();
                if (Peers.TryAdd((ushort)newPeer.Id, newPeer))
                {
                    BasisPlayerArray.AddPlayer(newPeer);
                    BNL.Log($"Peer connected: {newPeer.Id}");
                    ReadyMessage readyMessage = new ReadyMessage();
                    readyMessage.Deserialize(request.Data);
                    if (readyMessage.WasDeserializedCorrectly())
                    {
                        SendRemoteSpawnMessage(newPeer, readyMessage);
                    }
                    else
                    {
                        RejectWithReason(request, "Payload Provided was invalid!");
                    }
                }
                else
                {
                    RejectWithReason(request, "Peer already exists.");
                }
            }
        }
        catch (Exception e)
        {
            RejectWithReason(request, "Fatal Connection Issue stacktrace on server " + e.Message);
            BNL.LogError(e.StackTrace);
        }
    }
    private static void HandlePeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        try
        {
            ushort id = (ushort)peer.Id;
            ClientDisconnect(id, Peers);

            BasisPlayerArray.RemovePlayer(peer);
            if (Peers.TryRemove(id, out _))
            {
                BNL.Log($"Peer removed: {id}");
            }
            else
            {
                BNL.LogError($"Failed to remove peer: {id}");
            }
            CleanupPlayerData(id, peer);
        }
        catch (Exception e)
        {
            BNL.LogError(e.Message + " " + e.StackTrace);
        }
    }

    private static void CleanupPlayerData(ushort id, NetPeer peer)
    {
        BasisNetworkOwnership.RemovePlayerOwnership(id);
        BasisSavedState.RemovePlayer(peer);
        BasisServerReductionSystem.RemovePlayer(peer);
    }
    #endregion
    #region Network Receive Handlers
    private static void HandleNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            switch (channel)
            {
                case BasisNetworkCommons.FallChannel:
                    if (deliveryMethod == DeliveryMethod.Unreliable)
                    {
                        if (reader.TryGetByte(out byte Byte))
                        {
                            //  BNL.Log($"Found Channel {Byte} {reader.AvailableBytes}");
                            HandleNetworkReceiveEvent(peer, reader, Byte, deliveryMethod);
                        }
                        else
                        {
                            BNL.LogError($"Unknown channel no data remains: {channel} " + reader.AvailableBytes);
                            reader.Recycle();
                        }
                    }
                    else
                    {
                        BNL.LogError($"Unknown channel: {channel} " + reader.AvailableBytes);
                        reader.Recycle();
                    }
                    break;
                case BasisNetworkCommons.MovementChannel:
                    HandleAvatarMovement(reader, peer);
                    reader.Recycle();
                    break;
                case BasisNetworkCommons.VoiceChannel:
                    HandleVoiceMessage(reader, peer);
                    reader.Recycle();
                    break;
                case BasisNetworkCommons.AvatarChannel:
                    BasisNetworkingGeneric.HandleAvatar(reader, deliveryMethod, peer);
                    reader.Recycle();
                    break;
                case BasisNetworkCommons.SceneChannel:
                    BasisNetworkingGeneric.HandleScene(reader, deliveryMethod, peer);
                    reader.Recycle();
                    break;
                case BasisNetworkCommons.AvatarChangeMessage:
                    SendAvatarMessageToClients(reader, peer);
                    reader.Recycle();
                    break;
                case BasisNetworkCommons.OwnershipTransfer:
                    BasisNetworkOwnership.OwnershipTransfer(reader, peer);
                    reader.Recycle();
                    break;
                case BasisNetworkCommons.OwnershipResponse:
                    BasisNetworkOwnership.OwnershipResponse(reader, peer);
                    reader.Recycle();
                    break;
                case BasisNetworkCommons.AudioRecipients:
                    UpdateVoiceReceivers(reader, peer);
                    reader.Recycle();
                    break;
                default:
                    BNL.LogError($"Unknown channel: {channel} " + reader.AvailableBytes);
                    reader.Recycle();
                    break;
            }
        }
        catch (Exception e)
        {
            BNL.LogError($"{e.Message} : {e.StackTrace}");
            if (reader != null)
            {
                reader.Recycle();
            }
        }
    }

    #endregion

    #region Utility Methods
    private static void RejectWithReason(ConnectionRequest request, string reason)
    {
        NetDataWriter writer = NetDataWriterPool.GetWriter();
        writer.Put(reason);
        request.Reject(writer);
        BNL.LogError($"Rejected: {reason}");
        NetDataWriterPool.ReturnWriter(writer);
    }

    public static void ClientDisconnect(ushort leaving, ConcurrentDictionary<ushort, NetPeer> authenticatedClients)
    {
        NetDataWriter writer = NetDataWriterPool.GetWriter(sizeof(ushort));
        writer.Put(leaving);

        foreach (var client in authenticatedClients.Values)
        {
            if (client.Id != leaving)
            {
                client.Send(writer, BasisNetworkCommons.Disconnection, DeliveryMethod.ReliableOrdered);
            }
        }
        NetDataWriterPool.ReturnWriter(writer);
    }
    #endregion
    private static void SendAvatarMessageToClients(NetPacketReader Reader, NetPeer Peer)
    {
        ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage();
        ClientAvatarChangeMessage.Deserialize(Reader);
        ServerAvatarChangeMessage serverAvatarChangeMessage = new ServerAvatarChangeMessage
        {
            clientAvatarChangeMessage = ClientAvatarChangeMessage,
            uShortPlayerId = new PlayerIdMessage
            {
                playerID = (ushort)Peer.Id
            }
        };
        BasisSavedState.AddLastData(Peer, ClientAvatarChangeMessage);
        NetDataWriter Writer = NetDataWriterPool.GetWriter();
        serverAvatarChangeMessage.Serialize(Writer);
        BroadcastMessageToClients(Writer, BasisNetworkCommons.AvatarChangeMessage, Peer,BasisPlayerArray.GetSnapshot());
        NetDataWriterPool.ReturnWriter(Writer);
    }
    private static void UpdateVoiceReceivers(NetPacketReader Reader, NetPeer Peer)
    {
        VoiceReceiversMessage VoiceReceiversMessage = new VoiceReceiversMessage();
        VoiceReceiversMessage.Deserialize(Reader);
        BasisSavedState.AddLastData(Peer, VoiceReceiversMessage);
    }
    private static void HandleVoiceMessage(NetPacketReader Reader, NetPeer peer)
    {
        AudioSegmentDataMessage audioSegment = new AudioSegmentDataMessage();
        audioSegment.Deserialize(Reader);
        ServerAudioSegmentMessage ServerAudio = new ServerAudioSegmentMessage
        {
            audioSegmentData = audioSegment
        };
        SendVoiceMessageToClients(ServerAudio, BasisNetworkCommons.VoiceChannel, peer);
    }
    private static void SendVoiceMessageToClients(ServerAudioSegmentMessage audioSegment, byte channel, NetPeer sender)
    {
        if (BasisSavedState.GetLastData(sender, out StoredData data))
        {
            if (data.voiceReceiversMessage.users == null)
            {
                // BNL.Log("No Users!");
                return;
            }

            int count = data.voiceReceiversMessage.users.Length;
            if (count == 0)
            {
                //  BNL.Log("No Count!");
                return;
            }
            List<NetPeer> endPoints = new List<NetPeer>(count);
            foreach (ushort user in data.voiceReceiversMessage.users)
            {
                if (Peers.TryGetValue(user, out NetPeer client))
                {
                    endPoints.Add(client);
                }
            }

            if (endPoints.Count == 0)
            {
                //  BNL.Log("No Viable");
                return;
            }

            audioSegment.playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)sender.Id
            };
            NetDataWriter NetDataWriter = NetDataWriterPool.GetWriter();
            audioSegment.Serialize(NetDataWriter);
            //  BNL.Log("Sending Voice Data To Clients");
            BroadcastMessageToClients(NetDataWriter, channel,ref endPoints, DeliveryMethod.Sequenced);
            NetDataWriterPool.ReturnWriter(NetDataWriter);
        }
        else
        {
            BNL.Log("Error unable to find " + sender.Id + " in the data store!");
        }
    }
    public static void BroadcastMessageToClients(NetDataWriter Reader, byte channel, NetPeer sender, ReadOnlySpan<NetPeer> authenticatedClients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced)
    {
        foreach (NetPeer client in authenticatedClients)
        {
            if (client.Id != sender.Id)
            {
                client.Send(Reader, channel, deliveryMethod);
            }
        }
    }
    public static void BroadcastMessageToClients(NetDataWriter Reader, byte channel, ReadOnlySpan<NetPeer> authenticatedClients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced)
    {
        int count = authenticatedClients.Length;
        for (int index = 0; index < count; index++)
        {
            authenticatedClients[index].Send(Reader, channel, deliveryMethod);
        }
    }
    public static void BroadcastMessageToClients(NetDataWriter Reader, byte channel,ref List<NetPeer> authenticatedClients, DeliveryMethod deliveryMethod = DeliveryMethod.Sequenced)
    {
        int count = authenticatedClients.Count;
        for (int index = 0; index < count; index++)
        {
            authenticatedClients[index].Send(Reader, channel, deliveryMethod);
        }
    }
    private static void HandleAvatarMovement(NetPacketReader Reader, NetPeer Peer)
    {
        LocalAvatarSyncMessage LocalAvatarSyncMessage = new LocalAvatarSyncMessage();
        LocalAvatarSyncMessage.Deserialize(Reader);
        BasisSavedState.AddLastData(Peer, LocalAvatarSyncMessage);
        ReadOnlySpan<NetPeer> Peers = BasisPlayerArray.GetSnapshot();
        foreach (NetPeer client in Peers)
        {
            if (client.Id == Peer.Id)
            {
                continue;
            }
            ServerSideSyncPlayerMessage ssspm = CreateServerSideSyncPlayerMessage(LocalAvatarSyncMessage, (ushort)Peer.Id);
            BasisServerReductionSystem.AddOrUpdatePlayer(client, ssspm, Peer);
        }
    }
    private static ServerSideSyncPlayerMessage CreateServerSideSyncPlayerMessage(LocalAvatarSyncMessage local, ushort clientId)
    {
        return new ServerSideSyncPlayerMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = clientId },
            avatarSerialization = local
        };
    }
    public static void SendRemoteSpawnMessage(NetPeer authClient, ReadyMessage readyMessage)
    {
        ServerReadyMessage serverReadyMessage = LoadInitialState(authClient, readyMessage);
        NotifyExistingClients(serverReadyMessage, authClient);
        SendClientListToNewClient(authClient);
    }
    public static ServerReadyMessage LoadInitialState(NetPeer authClient, ReadyMessage readyMessage)
    {
        ServerReadyMessage serverReadyMessage = new ServerReadyMessage
        {
            localReadyMessage = readyMessage,
            playerIdMessage = new PlayerIdMessage() { playerID = (ushort)authClient.Id },

        };
        BasisSavedState.AddLastData(authClient, readyMessage);
        return serverReadyMessage;
    }
    private static void NotifyExistingClients(ServerReadyMessage serverSideSyncPlayerMessage, NetPeer authClient)
    {
        NetDataWriter Writer = NetDataWriterPool.GetWriter();
        serverSideSyncPlayerMessage.Serialize(Writer);
        ReadOnlySpan<NetPeer> Peers = BasisPlayerArray.GetSnapshot();
        // string ClientIds = string.Empty;
        foreach (NetPeer client in Peers)
        {
            if (client != authClient)
            {
                //  ClientIds += $" | {client.Id}";
                client.Send(Writer, BasisNetworkCommons.CreateRemotePlayer, DeliveryMethod.ReliableOrdered);
            }
        }
        NetDataWriterPool.ReturnWriter(Writer);
        //   BNL.Log($"Sent Remote Spawn request to {ClientIds}");
    }
    private static void SendClientListToNewClient(NetPeer authClient)
    {
        if (Peers.Count > ushort.MaxValue)
        {
            BNL.Log($"authenticatedClients count exceeds {ushort.MaxValue}");
            return;
        }

        List<ServerReadyMessage> copied = new List<ServerReadyMessage>();

        IEnumerable<NetPeer> clientsToNotify = Peers.Values.Where(client => client != authClient);
        BNL.Log("Notifing Newly Connected Client about " + clientsToNotify.Count());
        foreach (NetPeer client in clientsToNotify)
        {
            ServerReadyMessage serverReadyMessage = new ServerReadyMessage();

            if (BasisSavedState.GetLastData(client, out StoredData sspm))
            {
                serverReadyMessage.localReadyMessage = new ReadyMessage
                {
                    localAvatarSyncMessage = sspm.lastAvatarSyncState,
                    clientAvatarChangeMessage = sspm.lastAvatarChangeState,
                    playerMetaDataMessage = sspm.playerMetaDataMessage,
                };
                serverReadyMessage.playerIdMessage = new PlayerIdMessage() { playerID = (ushort)client.Id };
            }
            else
            {
                BNL.Log("Unable to get last Data Creating Fake");
                serverReadyMessage.playerIdMessage = new PlayerIdMessage { playerID = (ushort)client.Id };
                serverReadyMessage.localReadyMessage = new ReadyMessage
                {
                    localAvatarSyncMessage = new LocalAvatarSyncMessage() { array = new byte[386] },
                    clientAvatarChangeMessage = new ClientAvatarChangeMessage() { byteArray = new byte[] { }, },
                    playerMetaDataMessage = new PlayerMetaDataMessage() { playerDisplayName = "Error", playerUUID = string.Empty },
                };
            }

            copied.Add(serverReadyMessage);
        }

        CreateAllRemoteMessage remoteMessages = new CreateAllRemoteMessage
        {
            serverSidePlayer = copied.ToArray(),
        };
        NetDataWriter Writer = NetDataWriterPool.GetWriter();
        remoteMessages.Serialize(Writer);
        BNL.Log($"Sending list of clients to {authClient.Id}");
        authClient.Send(Writer, BasisNetworkCommons.CreateRemotePlayers, DeliveryMethod.ReliableOrdered);
        NetDataWriterPool.ReturnWriter(Writer);
    }
}
