using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LiteNetLib.Utils;
using EntityClassData = LiteEntitySystem.EntityManager.EntityClassData;
using InternalEntity = LiteEntitySystem.EntityManager.InternalEntity;

namespace LiteEntitySystem
{
    internal sealed class StateSerializer
    {
        private byte _version;

        private const int MaxHistory = 32; //should be power of two
        private const int MaxFields = 31;
        public const int HeaderSize = 9;
        public const int DiffHeaderSize = 8;
        public const uint AllFields = uint.MaxValue;

        private EntityClassData _classData;
        private InternalEntity _entityLogic;
        private readonly NetDataWriter[] _history = new NetDataWriter[MaxHistory];
        private byte[] _latestEntityData;
        private readonly ushort[] _fieldChangeTicks = new ushort[MaxFields];
        private ushort _versionChangedTick;

        public byte IncrementVersion(ushort tick)
        {
            _versionChangedTick = tick;
            return _version++;
        }

        internal void Init(EntityClassData classData, InternalEntity e)
        {
            _classData = classData;
            _entityLogic = e;
            if (classData.IsServerOnly)
                return;
            _stringHashcodes.Clear();
            _latestStrings.Clear();

            int dataSize = HeaderSize + _classData.FixedFieldsSize;
            if (_latestEntityData == null)
                _latestEntityData = new byte[dataSize];
            else if(_latestEntityData.Length < dataSize)
                Array.Resize(ref _latestEntityData, dataSize);

            //only in diff
            FastBitConverter.GetBytes(_latestEntityData, 0, e.Id);
            FastBitConverter.GetBytes(_latestEntityData, 2, AllFields);
            //full data
            _latestEntityData[6] = e.Version;
            FastBitConverter.GetBytes(_latestEntityData, 7, e.ClassId);

            //only if revertable
            if(_history[0] == null)
                for (int i = 0; i < MaxHistory; i++)
                    _history[i] = new NetDataWriter(true, HeaderSize); 
        }

        private readonly List<NetDataWriter> _latestStrings = new List<NetDataWriter>();
        private readonly List<int> _stringHashcodes = new List<int>();
        private int _stringIndex;

        public void ResetStringIndex()
        {
            _stringIndex = -1;
        }

        public void IncrementStringIndex()
        {
            _stringIndex++;
        }

        public bool WriteString(ref string str)
        {
            int strHashCode = str.GetHashCode();
            _stringIndex++;

            if (_latestStrings.Count <= _stringIndex)
            {
                _stringHashcodes.Add(strHashCode);
                var stringWriter = new NetDataWriter();
                stringWriter.Put(str);
                _latestStrings.Add(stringWriter);
            }
            else if (strHashCode == _stringHashcodes[_stringIndex])
            {
                return false;
            }
            else
            {
                _stringHashcodes[_stringIndex] = strHashCode;
                var stringWriter = _latestStrings[_stringIndex];
                stringWriter.Reset();
                stringWriter.Put(str);
            }

            return true;
        }
        
        public unsafe void WritePredicted(int latestDataOffset, byte* data, uint size)
        {
            fixed (byte* latestEntityData = _latestEntityData)
            {
                Unsafe.CopyBlock(latestEntityData + HeaderSize + latestDataOffset, data, size);
            }
        }

        public unsafe void Write(ushort currentTick)
        {
            if (_classData.IsServerOnly)
                return;

            byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entityLogic);
            int offset = HeaderSize;
            _stringIndex = -1;

            fixed (byte* latestEntityData = _latestEntityData)
            {
                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    ref var entityFieldInfo = ref _classData.Fields[i];
                    byte* fieldPtr = entityPointer + entityFieldInfo.Offset;
                    byte* latestDataPtr = latestEntityData + offset;

                    //update only changed fields
                    switch (entityFieldInfo.Type)
                    {
                        case FixedFieldType.None:
                            if (Utils.memcmp(latestDataPtr, fieldPtr, entityFieldInfo.PtrSize) != 0)
                            {
                                Unsafe.CopyBlock(latestDataPtr, fieldPtr, entityFieldInfo.Size);
                                _fieldChangeTicks[i] = currentTick;
                            }
                            offset += entityFieldInfo.IntSize;
                            break;
                        
                        case FixedFieldType.EntityId:
                            ushort entityId = Unsafe.AsRef<EntityLogic>(fieldPtr)?.Id ?? EntityManager.InvalidEntityId;
                            ushort *ushortPtr = (ushort*)latestDataPtr;
                            if (*ushortPtr != entityId)
                            {
                                *ushortPtr = entityId;
                                _fieldChangeTicks[i] = currentTick;
                            }
                            offset += 2;
                            break;
                        
                        case FixedFieldType.String:
                            ref string str = ref Unsafe.AsRef<string>(fieldPtr);
                            if(WriteString(ref str))
                                _fieldChangeTicks[i] = currentTick;
                            break;
                    }
                }
            }
        }

        public unsafe int MakeDiff(ushort playerTick, NetDataWriter result, bool isFullSync)
        {
            if (_classData.IsServerOnly)
                return -1;
            
            int totalSizePos = result.Length;
            int resultOffset;
            
            int lastDataOffset = HeaderSize;
            int stringIndex = 0;
            bool writeMaxFields = false;
            
            fixed (byte* lastEntityData = _latestEntityData, resultData = result.Data)
            {
                uint* fields;
                if (isFullSync)
                {
                    //dont write total size in full sync
                    //totalSizePos here equal to EID position
                    fields = (uint*)(resultData + totalSizePos + sizeof(ushort));
                    Unsafe.CopyBlock(resultData + totalSizePos, lastEntityData, HeaderSize);
                    resultOffset = totalSizePos + HeaderSize;//- size
                    writeMaxFields = true;
                }
                else if (EntityManager.SequenceDiff(_versionChangedTick, playerTick) > 0)
                {
                    //write full header here (totalSize + eid)
                    fields = (uint*)(resultData + totalSizePos + sizeof(ushort) + sizeof(ushort));
                    Unsafe.CopyBlock(resultData + totalSizePos + sizeof(ushort), lastEntityData, HeaderSize);
                    resultOffset = totalSizePos + sizeof(ushort) + HeaderSize;
                    writeMaxFields = true;
                }
                else
                {
                    int entityIdPos = totalSizePos + sizeof(ushort);
                    fields = (uint*)(resultData + entityIdPos + sizeof(ushort));
                    resultOffset = totalSizePos + DiffHeaderSize;
                    
                    //put entity id
                    *(ushort*)(resultData + entityIdPos) = *(ushort*)lastEntityData;
                }
                
                *fields = 0;

                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    ref var fixedFieldInfo = ref _classData.Fields[i];
                    if (isFullSync || EntityManager.SequenceDiff(_fieldChangeTicks[i], playerTick) > 0)
                    {
                        *fields |= 1u << i;
                        if (fixedFieldInfo.Type == FixedFieldType.String)
                        {
                            var stringData = _latestStrings[stringIndex++];
                            fixed(byte* latestString = stringData.Data)
                                Unsafe.CopyBlock(resultData + resultOffset, latestString, (uint)stringData.Length);
                            resultOffset += stringData.Length;
                        }
                        else
                        {
                            Unsafe.CopyBlock(resultData + resultOffset, lastEntityData + lastDataOffset, fixedFieldInfo.Size);
                            resultOffset += fixedFieldInfo.IntSize;
                        }
                    }
                    lastDataOffset += fixedFieldInfo.IntSize;
                }
                //no changes
                if (*fields == 0)
                    return -1;

                if (writeMaxFields)
                {
                    *fields = AllFields;
                }
                if(!isFullSync)
                {
                    //write totalSize
                    int resultSize = resultOffset - result.Length;
                    if (resultSize > ushort.MaxValue)
                    {
                        //request full sync
                        return -1;
                    }
                    *(ushort*)(resultData + totalSizePos) = (ushort)resultSize;
                }
            }

            result.SetPosition(resultOffset);
            return resultOffset;
        }
    }
}