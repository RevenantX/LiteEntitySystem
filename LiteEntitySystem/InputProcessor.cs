using System;
using LiteEntitySystem.Collections;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public abstract class InputProcessor
    {
        private const int FieldsDivision = 2;
        
        public abstract void ReadInput(EntityManager manager, byte ownerId, ReadOnlySpan<byte> inputsData);
        public abstract void GenerateAndApplyInput(EntityManager manager, byte ownerId, InputPacketHeader inputPacketHeader);
        public abstract void ReadClientRequest(EntityManager manager, NetDataReader reader);
        
        public abstract void ClearClientStoredInputs();
        public abstract void RemoveClientProcessedInputs(ushort processedTick);
        public abstract int ClientStoredInputsCount { get; }
        public abstract (InputPacketHeader header, ushort tick) GetStoredInputInfo(int index);
        public abstract void WriteStoredInputData(int index, Span<byte> target);
        public abstract void WriteStoredInputHeader(int index, Span<byte> target);
        public abstract void ReadStoredInput(EntityManager manager, byte ownerId, int index);
        public abstract int DeltaEncode(int prevInputIndex, int currentInputIndex, Span<byte> result);
        
        private readonly byte[] _firstFullInput;
        
        public readonly int InputSize;
        public readonly int InputSizeWithHeader;
        public readonly int DeltaBits;
        public readonly int MaxDeltaSize;
        public readonly int MinDeltaSize;

        protected unsafe InputProcessor(int inputFixedSize)
        {
            //TODO: add later controller id for splitscreen
            InputSize = inputFixedSize;
            InputSizeWithHeader = InputSize + sizeof(InputPacketHeader);
            DeltaBits = InputSize / FieldsDivision + (InputSize % FieldsDivision == 0 ? 0 : 1);
            MinDeltaSize = DeltaBits / 8 + (DeltaBits % 8 == 0 ? 0 : 1);
            MaxDeltaSize = MinDeltaSize + InputSize;
            _firstFullInput = new byte[InputSize];
        }

        public int DeltaEncode(ReadOnlySpan<byte> prevInput, ReadOnlySpan<byte> currentInput, Span<byte> result)
        {
            var deltaFlags = new BitSpan(result, DeltaBits);
            deltaFlags.Clear();
            int resultSize = MinDeltaSize;
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
        
        public void DeltaDecodeInit(ReadOnlySpan<byte> fullInput) => 
            fullInput.CopyTo(_firstFullInput);

        public int DeltaDecode(ReadOnlySpan<byte> currentDeltaInput, Span<byte> result)
        {
            var deltaFlags = new BitReadOnlySpan(currentDeltaInput, DeltaBits);
            int fieldOffset = MinDeltaSize;
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

    public unsafe class InputProcessor<TInput> : InputProcessor where TInput : unmanaged
    {
        private const int InputBufferSize = 64;
        private readonly CircularBuffer<InputCommand> _inputCommands = new (InputBufferSize);
        
        struct InputCommand
        {
            public ushort Tick;
            public InputPacketHeader Header;
            public TInput Data;

            public InputCommand(ushort tick, TInput data, InputPacketHeader header)
            {
                Tick = tick;
                Data = data;
                Header = header;
            }
        }
        
        public override void ClearClientStoredInputs() => _inputCommands.Clear();
        
        public override int ClientStoredInputsCount => _inputCommands.Count;
        
        public override void RemoveClientProcessedInputs(ushort processedTick)
        {
            while (_inputCommands.Count > 0 && Utils.SequenceDiff(processedTick, _inputCommands.Front().Tick) >= 0)
                _inputCommands.PopFront();
        }

        public override (InputPacketHeader header, ushort tick) GetStoredInputInfo(int index)
        {
            ref var input = ref _inputCommands[index];
            return (input.Header, input.Tick);
        }

        public override void WriteStoredInputData(int index, Span<byte> target) => target.WriteStruct(_inputCommands[index].Data);

        public override void WriteStoredInputHeader(int index, Span<byte> target) => target.WriteStruct(_inputCommands[index].Header);
        
        public override void ReadStoredInput(EntityManager manager, byte ownerId, int index)
        {
            var input = _inputCommands[index].Data;
            foreach (var controller in manager.GetEntities<HumanControllerLogic<TInput>>())
            {
                if(controller.InternalOwnerId.Value != ownerId)
                    continue;
                controller.CurrentInput = input;
                return;
            }
        }

        public override int DeltaEncode(int prevInputIndex, int currentInputIndex, Span<byte> result)
        {
            fixed (void* ptrA = &_inputCommands[prevInputIndex].Data, ptrB = &_inputCommands[currentInputIndex].Data)
            {
                return DeltaEncode(new ReadOnlySpan<byte>(ptrA, InputSize), new ReadOnlySpan<byte>(ptrB, InputSize),
                    result);
            }
        }

        public InputProcessor() : base(sizeof(TInput))
        {
            
        }

        public override void ReadInput(EntityManager manager, byte ownerId, ReadOnlySpan<byte> inputsData)
        {
            fixed (byte* rawData = inputsData)
            {
                foreach (var controller in manager.GetEntities<HumanControllerLogic<TInput>>())
                {
                    if(controller.InternalOwnerId.Value != ownerId)
                        continue;
                    controller.CurrentInput = *(TInput*)rawData;
                    return;
                }
            }
        }

        public override void GenerateAndApplyInput(EntityManager manager, byte ownerId, InputPacketHeader inputPacketHeader)
        {
            //if no controller just put zeroes for simplicity
            TInput inputData = default;
            foreach (var controller in manager.GetEntities<HumanControllerLogic<TInput>>())
            {
                if(controller.InternalOwnerId.Value != ownerId)
                    continue;
                controller.GenerateInput(out inputData);
                controller.CurrentInput = inputData;
                break;
            }
            _inputCommands.PushBack(new InputCommand(manager.Tick, inputData, inputPacketHeader));
        }

        public override void ReadClientRequest(EntityManager manager, NetDataReader reader)
        {
            ushort controllerId = reader.GetUShort();
            byte controllerVersion = reader.GetByte();
            if (manager.TryGetEntityById<HumanControllerLogic<TInput>>(new EntitySharedReference(controllerId, controllerVersion), out var controller))
            {
                controller.ReadClientRequest(reader);
            }
        }
    }
}