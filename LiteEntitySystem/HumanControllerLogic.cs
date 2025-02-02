using System;
using System.Collections.Generic;
using LiteEntitySystem.Collections;
using LiteEntitySystem.Extensions;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public abstract class HumanControllerLogic : ControllerLogic
    {
        private struct EntitySyncInfo
        {
            public EntitySharedReference Entity;
            public bool SyncEnabled;
        }
        
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
        
        public const int StringSizeLimit = 1024;
        protected const int FieldsDivision = 2;
        
        //id + version + request id
        internal const int MinRequestSize = sizeof(ushort) + 1 + sizeof(ushort);
        
        private static RemoteCall<EntitySyncInfo> OnEntitySyncChangedRPC;
        
        //client requests
        private static RemoteCall<ServerResponse> ServerResponseRpc;
        
        private readonly NetPacketProcessor _packetProcessor = new(StringSizeLimit);
        private readonly NetDataWriter _requestWriter = new();
        private ushort _requestId;
        private readonly Dictionary<ushort,Action<bool>> _awaitingRequests;
        
        //entities that should be resynced after diffSync (server only)
        private readonly Dictionary<InternalEntity, ushort> _forceSyncEntities;
        private readonly SyncHashSet<EntitySharedReference> _skippedEntities = new();
        
        //input part
        private readonly byte[] _firstFullInput;
        
        internal readonly int InputSize;
        internal readonly int MaxInputDeltaSize;
        internal readonly int InputDeltaBits;
        internal readonly int MinInputDeltaSize;
        
        /// <summary>
        /// Change entity delta-diff synchronization for player that owns this controller
        /// constructor and destruction will be synchronized anyways
        /// works only on server
        /// </summary>
        /// <param name="entity">entity</param>
        /// <param name="enable">true - enable sync (if was disabled), disable otherwise</param>
        public void ChangeEntityDiffSync(EntityLogic entity, bool enable)
        {
            if (EntityManager.IsClient || entity == GetControlledEntity<PawnLogic>())
                return;
            if (!enable && !_skippedEntities.Contains(entity))
            {
                _skippedEntities.Add(entity);
                ExecuteRPC(OnEntitySyncChangedRPC, new EntitySyncInfo { Entity = entity, SyncEnabled = false });
            }
            else if (enable && _skippedEntities.Remove(entity))
            {
                ForceSyncEntity(entity);
                ExecuteRPC(OnEntitySyncChangedRPC, new EntitySyncInfo { Entity = entity, SyncEnabled = true });
            }
        }

        protected internal override void OnConstructed()
        {
            base.OnConstructed();
            
            //call OnEntityDiffSyncChanged for already skipped entities
            if(EntityManager.IsClient)
                foreach (var skippedEntity in _skippedEntities)
                    OnEntityDiffSyncChanged(EntityManager.GetEntityById<EntityLogic>(skippedEntity), false);
        }

        /// <summary>
        /// Is entity delta-diff synchronization disabled. Works on client and server
        /// </summary>
        /// <param name="entity">entity to check</param>
        /// <returns>true if entity sync is disabled</returns>
        public bool IsEntityDiffSyncDisabled(EntitySharedReference entity) =>
            _skippedEntities.Contains(entity) && !EntityManager.GetEntityById<EntityLogic>(entity).IsDestroyed;

        /// <summary>
        /// Enable diff sync for all entities that has disabled diff sync
        /// </summary>
        public void ResetEntitiesDiffSync()
        {
            if (EntityManager.IsClient)
                return;
            foreach (var entityRef in _skippedEntities)
            {
                var entity = EntityManager.GetEntityById<EntityLogic>(entityRef);
                if(entity == null)
                    continue;
                ForceSyncEntity(entity);
                ExecuteRPC(OnEntitySyncChangedRPC, new EntitySyncInfo { Entity = entity, SyncEnabled = false });
            }
            _skippedEntities.Clear();
        }

        //add to force sync list and trigger force entity sync in state serializer
        internal void ForceSyncEntity(InternalEntity entity)
        {
            _forceSyncEntities[entity] = EntityManager.Tick;
            ServerManager.ForceEntitySync(entity);
        }

        //is entity need force sync
        internal bool IsEntityNeedForceSync(InternalEntity entity, ushort playerTick)
        {
            if (!_forceSyncEntities.TryGetValue(entity, out var forceSyncTick))
                return false;

            if (Utils.SequenceDiff(playerTick, forceSyncTick) >= 0)
            {
                _forceSyncEntities.Remove(entity);
                return false;
            }

            return !entity.IsDestroyed;
        }

        private void OnEntityDestroyed(EntityLogic entityLogic)
        {
            _forceSyncEntities.Remove(entityLogic);
            if (_skippedEntities.Remove(entityLogic))
            {
                ServerManager.ForceEntitySync(entityLogic);
            }
        }
        
        protected HumanControllerLogic(EntityParams entityParams, int inputSize) : base(entityParams)
        {
            if (EntityManager.IsServer)
            {
                _forceSyncEntities = new Dictionary<InternalEntity, ushort>();
                EntityManager.GetEntities<EntityLogic>().OnDestroyed += OnEntityDestroyed;
            }
            else
                _awaitingRequests = new Dictionary<ushort, Action<bool>>();
            _requestWriter.Put(EntityManager.HeaderByte);
            _requestWriter.Put(InternalPackets.ClientRequest);
            _requestWriter.Put(entityParams.Header.Id);
            _requestWriter.Put(entityParams.Header.Version);

            InputSize = inputSize;
            InputDeltaBits = InputSize / FieldsDivision + (InputSize % FieldsDivision == 0 ? 0 : 1);
            MinInputDeltaSize = InputDeltaBits / 8 + (InputDeltaBits % 8 == 0 ? 0 : 1);
            MaxInputDeltaSize = MinInputDeltaSize + InputSize;
            _firstFullInput = new byte[InputSize];
        }
        
        protected override void OnDestroy()
        {
            if (EntityManager.IsServer)
                EntityManager.GetEntities<EntityLogic>().OnDestroyed -= OnEntityDestroyed;
            base.OnDestroy();
        }

        protected override void RegisterRPC(ref RPCRegistrator r)
        {
            base.RegisterRPC(ref r);
            r.CreateRPCAction((HumanControllerLogic c, EntitySyncInfo s) =>
            {
                c.OnEntityDiffSyncChanged(c.EntityManager.GetEntityById<EntityLogic>(s.Entity), s.SyncEnabled);
            }, ref OnEntitySyncChangedRPC, ExecuteFlags.SendToOwner);
            r.CreateRPCAction(this, OnServerResponse, ref ServerResponseRpc, ExecuteFlags.SendToOwner);
        }

        /// <summary>
        /// Called when entity diff sync changed (enabled or disabled)
        /// useful for hiding disabled entities
        /// </summary>
        /// <param name="entity">entity</param>
        /// <param name="enabled">sync enabled or disabled</param>
        protected virtual void OnEntityDiffSyncChanged(EntityLogic entity, bool enabled)
        {
            
        }

        /// <summary>
        /// Get player that uses this controller
        /// </summary>
        /// <returns>assigned player</returns>
        public NetPlayer GetAssignedPlayer()
        {
            if (InternalOwnerId.Value == EntityManager.ServerPlayerId)
                return null;
            if (EntityManager.IsClient)
                return ClientManager.LocalPlayer;

            return ServerManager.GetPlayer(InternalOwnerId);
        }

        private void OnServerResponse(ServerResponse response)
        {
            //Logger.Log($"OnServerResponse Id: {response.RequestId} - {response.Success}");
            if (_awaitingRequests.Remove(response.RequestId, out var action))
                action(response.Success);
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
        
        protected void RegisterClientCustomType<T>() where T : struct, INetSerializable =>
            _packetProcessor.RegisterNestedType<T>();

        protected void RegisterClientCustomType<T>(Action<NetDataWriter, T> writeDelegate, Func<NetDataReader, T> readDelegate) =>
            _packetProcessor.RegisterNestedType(writeDelegate, readDelegate);

        protected void SendRequest<T>(T request) where T : class, new()
        {
            if (EntityManager.InRollBackState)
            {
                Logger.LogWarning("SendRequest is ignored in Rollback mode");
                return;
            }
            _requestWriter.SetPosition(5);
            _requestWriter.Put(_requestId);
            _packetProcessor.Write(_requestWriter, request);
            _requestId++;
            ClientManager.NetPeer.SendReliableOrdered(new ReadOnlySpan<byte>(_requestWriter.Data, 0, _requestWriter.Length));
        }
        
        protected void SendRequest<T>(T request, Action<bool> onResult) where T : class, new()
        {
            if (EntityManager.InRollBackState)
            {
                Logger.LogWarning("SendRequest is ignored in Rollback mode");
                onResult(false);
                return;
            }
            _requestWriter.SetPosition(5);
            _requestWriter.Put(_requestId);
            _packetProcessor.Write(_requestWriter, request);
            _awaitingRequests[_requestId] = onResult;
            _requestId++;
            ClientManager.NetPeer.SendReliableOrdered(new ReadOnlySpan<byte>(_requestWriter.Data, 0, _requestWriter.Length));
        }
        
        protected void SendRequestStruct<T>(T request) where T : struct, INetSerializable
        {
            if (EntityManager.InRollBackState)
            {
                Logger.LogWarning("SendRequest is ignored in Rollback mode");
                return;
            }
            _requestWriter.SetPosition(5);
            _requestWriter.Put(_requestId);
            _packetProcessor.WriteNetSerializable(_requestWriter, ref request);
            _requestId++;
            ClientManager.NetPeer.SendReliableOrdered(new ReadOnlySpan<byte>(_requestWriter.Data, 0, _requestWriter.Length));
        }
        
        protected void SendRequestStruct<T>(T request, Action<bool> onResult) where T : struct, INetSerializable
        {
            if (EntityManager.InRollBackState)
            {
                Logger.LogWarning("SendRequest is ignored in Rollback mode");
                onResult(false);
                return;
            }
            _requestWriter.SetPosition(5);
            _requestWriter.Put(_requestId);
            _packetProcessor.WriteNetSerializable(_requestWriter, ref request);
            _awaitingRequests[_requestId] = onResult;
            _requestId++;
            ClientManager.NetPeer.SendReliableOrdered(new ReadOnlySpan<byte>(_requestWriter.Data, 0, _requestWriter.Length));
        }
        
        protected void SubscribeToClientRequestStruct<T>(Action<T> onRequestReceived) where T : struct, INetSerializable =>
            _packetProcessor.SubscribeNetSerializable<T, ushort>((data, requestId) =>
            {
                onRequestReceived(data);
                ExecuteRPC(ServerResponseRpc, new ServerResponse(requestId, true));
            });
        
        protected void SubscribeToClientRequestStruct<T>(Func<T, bool> onRequestReceived) where T : struct, INetSerializable =>
            _packetProcessor.SubscribeNetSerializable<T, ushort>((data, requestId) =>
            {
                bool success = onRequestReceived(data);
                ExecuteRPC(ServerResponseRpc, new ServerResponse(requestId, success));
            });
        
        protected void SubscribeToClientRequest<T>(Action<T> onRequestReceived) where T : class, new() =>
            _packetProcessor.SubscribeReusable<T, ushort>((data, requestId) =>
            {
                onRequestReceived(data);
                ExecuteRPC(ServerResponseRpc, new ServerResponse(requestId, true));
            });
        
        protected void SubscribeToClientRequest<T>(Func<T, bool> onRequestReceived) where T : class, new() =>
            _packetProcessor.SubscribeReusable<T, ushort>((data, requestId) =>
            {
                bool success = onRequestReceived(data);
                ExecuteRPC(ServerResponseRpc, new ServerResponse(requestId, success));
            });
        
        //input stuff
        internal abstract void AddIncomingInput(ushort tick, ReadOnlySpan<byte> inputsData);

        internal abstract void ApplyIncomingInput(ushort tick);
        
        internal abstract void ApplyPendingInput();
        
        internal abstract void ClearClientStoredInputs();
        
        internal abstract void RemoveClientProcessedInputs(ushort processedTick);
        
        internal abstract void WriteStoredInput(int index, Span<byte> target);
        
        internal abstract void ReadStoredInput(int index);
        
        internal abstract int DeltaEncode(int prevInputIndex, int currentInputIndex, Span<byte> result);
        
        internal void DeltaDecodeInit(ReadOnlySpan<byte> fullInput) => fullInput.CopyTo(_firstFullInput);

        internal int DeltaDecode(ReadOnlySpan<byte> currentDeltaInput, Span<byte> result)
        {
            var deltaFlags = new BitReadOnlySpan(currentDeltaInput, InputDeltaBits);
            int fieldOffset = MinInputDeltaSize;
            for (int i = 0; i < InputSize; i += FieldsDivision)
            {
                if (deltaFlags[i / 2])
                {
                    _firstFullInput[i] = result[i] = currentDeltaInput[fieldOffset];
                    if (i < InputSize - 1)
                        _firstFullInput[i+1] = result[i+1] = currentDeltaInput[fieldOffset+1];
                    fieldOffset += FieldsDivision;
                }
                else
                {
                    result[i] = _firstFullInput[i];
                    if(i < InputSize - 1)
                        result[i+1] = _firstFullInput[i+1];
                }
            }
            return fieldOffset;
        }
    }
    
    /// <summary>
    /// Base class for human Controller entities
    /// </summary>
    [EntityFlags(EntityFlags.UpdateOnClient)]
    public abstract class HumanControllerLogic<TInput> : HumanControllerLogic where TInput : unmanaged
    {
        struct InputCommand
        {
            public ushort Tick;
            public TInput Data;

            public InputCommand(ushort tick, TInput data)
            {
                Tick = tick;
                Data = data;
            }
        }
        
        public override bool IsBot => false;
        
        private TInput _currentInput;
        private TInput _pendingInput;
        private bool _shouldResetInput;
        
        //client part
        private readonly CircularBuffer<InputCommand> _inputCommands;
        
        //server part
        internal readonly SequenceBinaryHeap<TInput> AvailableInput;
        
        /// <summary>
        /// Get pending input reference for modifications
        /// Pending input resets to default after it was assigned to CurrentInput inside logic tick
        /// </summary>
        /// <returns>Pending input reference</returns>
        protected ref TInput ModifyPendingInput()
        {
            if(_shouldResetInput)
                _pendingInput = default;
            return ref _pendingInput;
        }

        /// <summary>
        /// Current player input
        /// On client created from PendingInput inside logic tick if ModifyInput called
        /// On server read from players
        /// </summary>
        public TInput CurrentInput
        {
            get => _currentInput;
            private set => _currentInput = value;
        }
        
        protected HumanControllerLogic(EntityParams entityParams) : base(entityParams, Utils.SizeOfStruct<TInput>())
        {
            if (IsClient)
            {
                _inputCommands = new(ClientEntityManager.InputBufferSize);
            }
            else
            {
                AvailableInput = new(ServerEntityManager.MaxStoredInputs);
            }
        }
        
        internal override void RemoveClientProcessedInputs(ushort processedTick)
        {
            while (_inputCommands.Count > 0 && Utils.SequenceDiff(processedTick, _inputCommands.Front().Tick) >= 0)
                _inputCommands.PopFront();
        }
        
        internal override void ClearClientStoredInputs() =>
            _inputCommands.Clear();

        internal override void WriteStoredInput(int index, Span<byte> target) =>
            target.WriteStruct(_inputCommands[index].Data);

        internal override void ReadStoredInput(int index) => 
            _currentInput = index < _inputCommands.Count 
                ? _inputCommands[index].Data 
                : default;

        internal override void ApplyPendingInput()
        {
            _shouldResetInput = true;
            _currentInput = _pendingInput;
            _inputCommands.PushBack(new InputCommand(EntityManager.Tick, _currentInput));
        }

        internal override unsafe void AddIncomingInput(ushort tick, ReadOnlySpan<byte> inputsData)
        {
            fixed (byte* rawData = inputsData)
            { 
                AvailableInput.AddAndOverwrite(*(TInput*)rawData, tick);
            }
        }

        internal override void ApplyIncomingInput(ushort tick)
        {
            int seqDiff;
            while (AvailableInput.Count > 0 && (seqDiff = Utils.SequenceDiff(AvailableInput.PeekMinWithSequence().sequence, tick)) <= 0)
            {
                if (seqDiff < 0)
                {
                    AvailableInput.ExtractMin();
                    //Logger.Log("OLD INPUT");
                } 
                else if (seqDiff == 0)
                {
                    //Set input if tick equals
                    CurrentInput = AvailableInput.ExtractMin();
                    break;
                }
            }
        }

        internal override unsafe int DeltaEncode(int prevInputIndex, int currentInputIndex, Span<byte> result)
        {
            fixed (void* ptrA = &_inputCommands[prevInputIndex].Data, ptrB = &_inputCommands[currentInputIndex].Data)
            {
                byte* prevInput = (byte*)ptrA;
                byte *currentInput = (byte*)ptrB;
                
                var deltaFlags = new BitSpan(result, InputDeltaBits);
                deltaFlags.Clear();
                int resultSize = MinInputDeltaSize;
                for (int i = 0; i < InputSize; i += FieldsDivision)
                {
                    if (prevInput[i] != currentInput[i] || (i < InputSize - 1 && prevInput[i + 1] != currentInput[i + 1]))
                    {
                        deltaFlags[i / FieldsDivision] = true;
                        result[resultSize] = currentInput[i];
                        if(i < InputSize - 1)
                            result[resultSize + 1] = currentInput[i + 1];
                        resultSize += FieldsDivision;
                    }
                }
                return resultSize;
            }
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