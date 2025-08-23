using System;
using System.Collections.Generic;
using LiteEntitySystem.Collections;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public abstract class HumanControllerLogic : ControllerLogic
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
        
        public const int StringSizeLimit = 1024;
        protected const int FieldsDivision = 2;
        
        //id + version + request id
        internal const int MinRequestSize = sizeof(ushort) + 1 + sizeof(ushort);
        
        //client requests
        private static RemoteCall<ServerResponse> ServerResponseRpc;
        
        private readonly NetPacketProcessor _packetProcessor = new(StringSizeLimit);
        private readonly NetDataWriter _requestWriter = new();
        private ushort _requestId;
        private readonly Dictionary<ushort,Action<bool>> _awaitingRequests;
        
        //input part
        internal DeltaCompressor DeltaCompressor;

        public int InputSize => DeltaCompressor.Size;
        public int MinInputDeltaSize => DeltaCompressor.MinDeltaSize;
        public int MaxInputDeltaSize => DeltaCompressor.MaxDeltaSize;

        protected HumanControllerLogic(EntityParams entityParams, int inputSize) : base(entityParams)
        {
            DeltaCompressor = new DeltaCompressor(inputSize);
            
            if (EntityManager.IsClient)
                _awaitingRequests = new Dictionary<ushort, Action<bool>>();
            
            _requestWriter.Put(EntityManager.HeaderByte);
            _requestWriter.Put(InternalPackets.ClientRequest);
            _requestWriter.Put(entityParams.Id);
            _requestWriter.Put(entityParams.Header.Version);
        }

        protected override void RegisterRPC(ref RPCRegistrator r)
        {
            base.RegisterRPC(ref r);
            r.CreateRPCAction(this, OnServerResponse, ref ServerResponseRpc, ExecuteFlags.SendToOwner);
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
        
        internal abstract void ReadStoredInput(int index);

        internal abstract int DeltaEncode(int prevInputIndex, int currentInputIndex, Span<byte> result);
        
        internal void DeltaDecodeInit() =>
            DeltaCompressor.Init();

        internal int DeltaDecode(ReadOnlySpan<byte> currentDeltaInput, Span<byte> result) =>
            DeltaCompressor.Decode(currentDeltaInput, result);
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
        /// Returns default new input for custom cases where not all values should be 0
        /// </summary>
        /// <returns>New clean input</returns>
        protected virtual TInput GetDefaultInput() => default;
        
        /// <summary>
        /// Get pending input reference for modifications
        /// Pending input resets to default after it was assigned to CurrentInput inside logic tick
        /// </summary>
        /// <returns>Pending input reference</returns>
        protected ref TInput ModifyPendingInput()
        {
            if (_shouldResetInput)
            {
                _pendingInput = GetDefaultInput();
                _shouldResetInput = false;
            }
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

            // ReSharper disable once VirtualMemberCallInConstructor
            _currentInput = GetDefaultInput();
            _pendingInput = _currentInput;
        }
        
        internal override void RemoveClientProcessedInputs(ushort processedTick)
        {
            while (_inputCommands.Count > 0 && Utils.SequenceDiff(processedTick, _inputCommands.Front().Tick) >= 0)
                _inputCommands.PopFront();
        }
        
        internal override void ClearClientStoredInputs() =>
            _inputCommands.Clear();

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
                    _currentInput = AvailableInput.ExtractMin();
                    break;
                }
            }
        }

        internal override int DeltaEncode(int prevInputIndex, int currentInputIndex, Span<byte> result) =>
            prevInputIndex >= 0
                ? DeltaCompressor.Encode(ref _inputCommands[prevInputIndex].Data, ref _inputCommands[currentInputIndex].Data, result)
                : DeltaCompressor.Encode(ref _inputCommands[currentInputIndex].Data, result);
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