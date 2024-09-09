using System;
using System.Collections.Generic;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    /// <summary>
    /// Base class for human Controller entities
    /// </summary>
    [EntityFlags(EntityFlags.UpdateOnClient)]
    public abstract class HumanControllerLogic<TInput> : ControllerLogic where TInput : unmanaged
    {
        private struct ServerResponse
        {
            public ushort RequestId;
            public bool Success;

            public ServerResponse(ushort requestId, bool success)
            {
                RequestId = requestId;
                Success = success;
            }
        }
        
        /// <summary>
        /// Called on client and server to read generated from <see cref="GenerateInput"/> input
        /// </summary>
        /// <param name="input">user defined input structure</param>
        protected internal abstract void ReadInput(in TInput input);
        
        /// <summary>
        /// Called on client to generate input
        /// </summary>
        protected internal abstract void GenerateInput(out TInput input);

        public override bool IsBot => false;
        
        public const int StringSizeLimit = 1024;
        private readonly NetPacketProcessor _packetProcessor = new(StringSizeLimit);
        private readonly NetDataWriter _requestWriter = new();
        private static RemoteCall<ServerResponse> _serverResponseRpc;
        private ushort _requestId;
        private readonly Queue<(ushort,Action<bool>)> _awaitingRequests;

        public NetPlayer GetAssignedPlayer()
        {
            if (InternalOwnerId.Value == EntityManager.ServerPlayerId)
                return null;
            if (EntityManager.IsClient)
                return ClientManager.LocalPlayer;

            return ServerManager.GetPlayer(InternalOwnerId);
        }

        protected override void RegisterRPC(ref RPCRegistrator r)
        {
            base.RegisterRPC(ref r);
            r.CreateRPCAction(this, OnServerResponse, ref _serverResponseRpc, ExecuteFlags.SendToOwner);
        }

        private void OnServerResponse(ServerResponse response)
        {
            //Logger.Log($"OnServerResponse Id: {response.RequestId} - {response.Success}");
            while (_awaitingRequests.Count > 0)
            {
                var awaitingRequest = _awaitingRequests.Dequeue();
                int diff = Utils.SequenceDiff(response.RequestId, awaitingRequest.Item1);
                if (diff == 0)
                {
                    awaitingRequest.Item2(response.Success);
                }
                else if (diff < 0)
                {
                    awaitingRequest.Item2(false);
                }
                else
                {
                    Logger.LogError("Should be impossible");
                }
            }
        }

        internal void ReadClientRequest(NetDataReader dataReader)
        {
            try
            {
                ushort requestId = dataReader.GetUShort();
                _packetProcessor.ReadPacket(dataReader, requestId);
            }
            catch (Exception e)
            {
                Logger.LogError($"Received invalid data as request: {e}");
            }
        }
        
        protected void RegisterClientCustomType<T>() where T : struct, INetSerializable
        {
            _packetProcessor.RegisterNestedType<T>();
        }

        protected void RegisterClientCustomType<T>(Action<NetDataWriter, T> writeDelegate, Func<NetDataReader, T> readDelegate)
        {
            _packetProcessor.RegisterNestedType(writeDelegate, readDelegate);
        }

        protected void SendRequest<T>(T request) where T : class, new()
        {
            _requestWriter.SetPosition(5);
            _requestWriter.Put(_requestId);
            _packetProcessor.Write(_requestWriter, request);
            _requestId++;
            ClientManager.NetPeer.SendReliableOrdered(new ReadOnlySpan<byte>(_requestWriter.Data, 0, _requestWriter.Length));
        }
        
        protected void SendRequest<T>(T request, Action<bool> onResult) where T : class, new()
        {
            _requestWriter.SetPosition(5);
            _requestWriter.Put(_requestId);
            _packetProcessor.Write(_requestWriter, request);
            _awaitingRequests.Enqueue((_requestId, onResult));
            _requestId++;
            ClientManager.NetPeer.SendReliableOrdered(new ReadOnlySpan<byte>(_requestWriter.Data, 0, _requestWriter.Length));
        }
        
        protected void SendRequestStruct<T>(T request) where T : struct, INetSerializable
        {
            _requestWriter.SetPosition(5);
            _requestWriter.Put(_requestId);
            _packetProcessor.WriteNetSerializable(_requestWriter, ref request);
            _requestId++;
            ClientManager.NetPeer.SendReliableOrdered(new ReadOnlySpan<byte>(_requestWriter.Data, 0, _requestWriter.Length));
        }
        
        protected void SendRequestStruct<T>(T request, Action<bool> onResult) where T : struct, INetSerializable
        {
            _requestWriter.SetPosition(5);
            _requestWriter.Put(_requestId);
            _packetProcessor.WriteNetSerializable(_requestWriter, ref request);
            _awaitingRequests.Enqueue((_requestId, onResult));
            _requestId++;
            ClientManager.NetPeer.SendReliableOrdered(new ReadOnlySpan<byte>(_requestWriter.Data, 0, _requestWriter.Length));
        }
        
        protected void SubscribeToClientRequestStruct<T>(Action<T> onRequestReceived) where T : struct, INetSerializable
        {
            _packetProcessor.SubscribeNetSerializable<T, ushort>((data, requestId) =>
            {
                onRequestReceived(data);
                ExecuteRPC(_serverResponseRpc, new ServerResponse(requestId, true));
            });
        }
        
        protected void SubscribeToClientRequestStruct<T>(Func<T, bool> onRequestReceived) where T : struct, INetSerializable
        {
            _packetProcessor.SubscribeNetSerializable<T, ushort>((data, requestId) =>
            {
                bool success = onRequestReceived(data);
                ExecuteRPC(_serverResponseRpc, new ServerResponse(requestId, success));
            });
        }
        
        protected void SubscribeToClientRequest<T>(Action<T> onRequestReceived) where T : class, new()
        {
            _packetProcessor.SubscribeReusable<T, ushort>((data, requestId) =>
            {
                onRequestReceived(data);
                ExecuteRPC(_serverResponseRpc, new ServerResponse(requestId, true));
            });
        }
        
        protected void SubscribeToClientRequest<T>(Func<T, bool> onRequestReceived) where T : class, new()
        {
            _packetProcessor.SubscribeReusable<T, ushort>((data, requestId) =>
            {
                bool success = onRequestReceived(data);
                ExecuteRPC(_serverResponseRpc, new ServerResponse(requestId, success));
            });
        }

        protected HumanControllerLogic(EntityParams entityParams) : base(entityParams)
        {
            if (EntityManager.IsClient)
            {
                _awaitingRequests = new Queue<(ushort, Action<bool>)>();
            }
            _requestWriter.Put(EntityManager.HeaderByte);
            _requestWriter.Put(InternalPackets.ClientRequest);
            _requestWriter.Put(entityParams.Id);
            _requestWriter.Put(entityParams.Version);
        }
    }

    /// <summary>
    /// Base class for human Controller entities with typed ControlledEntity field
    /// </summary>
    public abstract class HumanControllerLogic<TInput, T> : HumanControllerLogic<TInput> where T : PawnLogic where TInput : unmanaged
    {
        public T ControlledEntity => GetControlledEntity<T>();

        protected HumanControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }
}