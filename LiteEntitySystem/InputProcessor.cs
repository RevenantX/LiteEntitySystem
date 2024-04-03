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
        public abstract void GenerateAndWriteInput(EntityManager manager, byte ownerId, byte[] data, int offset);
        public abstract void ReadClientRequest(EntityManager manager, NetDataReader reader);
      
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
        
        public void DeltaDecodeInit(ReadOnlySpan<byte> fullInput)
        {
            fullInput.CopyTo(_firstFullInput);
        }

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
        public InputProcessor() : base(sizeof(TInput))
        {
            
        }

        public override void ReadInput(EntityManager manager, byte ownerId, ReadOnlySpan<byte> inputsData)
        {
            fixed (byte* rawData = inputsData)
            {
                foreach (var controller in manager.GetControllers<HumanControllerLogic<TInput>>())
                {
                    if(controller.InternalOwnerId.Value != ownerId)
                        continue;
                    controller.ReadInput(*(TInput*)rawData);
                    return;
                }
            }
        }

        public override void GenerateAndWriteInput(EntityManager manager, byte ownerId, byte[] data, int offset)
        {
            fixed (byte* rawData = data)
            {
                foreach (var controller in manager.GetControllers<HumanControllerLogic<TInput>>())
                {
                    if(controller.InternalOwnerId.Value != ownerId)
                        continue;
                    controller.GenerateInput(out var input);
                    *(TInput*)(rawData + offset) = input;
                    return;
                }
                //if no controller just put zeroes for simplicity
                *(TInput*)(rawData + offset) = default(TInput);
            }
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