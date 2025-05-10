using System;

namespace LiteEntitySystem
{
    [Flags]
    public enum SyncGroup : byte
    {
        SyncGroup1 = 1,
        SyncGroup2 = 1 << 1,
        SyncGroup3 = 1 << 2,
        SyncGroup4 = 1 << 3,
        SyncGroup5 = 1 << 4,
        
        All = 0x1F
    }
    
    internal struct SyncGroupData
    {
        public ushort LastChangedTick;
        public SyncGroup EnabledGroups;
        public readonly bool IsInitialized;

        public SyncGroupData(ushort lastChangedTick)
        {
            IsInitialized = true;
            LastChangedTick = lastChangedTick;
            EnabledGroups = SyncGroup.All;
        }

        public bool IsGroupEnabled(SyncGroup group) => 
            !IsInitialized || EnabledGroups.HasFlagFast(group);

        public void SetGroupEnabled(SyncGroup group, bool enabled)
        {
            byte bitMask = (byte)EnabledGroups;
            EnabledGroups = (SyncGroup)(enabled 
                ? bitMask | (1 << (byte)group)
                : bitMask & ~(1 << (byte)group));
        }
    }

    public static class SyncGroupUtils
    {
        public const int SyncGroupsCount = 5;

        public static SyncFlags ToSyncFlags(SyncGroup sv)
        {
            int bitMask = (byte)sv & 0x1F; //first 5 bits
            SyncFlags result = bitMask == 0 
                ? SyncFlags.None
                : (SyncFlags)(bitMask << 6); //because SyncFlags.SyncGroup1 is 1 << 6
            //Logger.Log($"SyncGroup: {Convert.ToString(bitMask, 2).PadLeft(16, '0')}, SyncFlags: {Convert.ToString((ushort)result, 2).PadLeft(16, '0')}");
            return result;
        }

        public static ExecuteFlags ToExecuteFlag(SyncGroup sv)
        {
            int bitMask = (byte)sv & 0x1F; //first 5 bits
            ExecuteFlags result = bitMask == 0 
                ? ExecuteFlags.None
                : (ExecuteFlags)(bitMask << 4); //because ExecuteFlags.SyncGroup1 is 1 << 4
            //Logger.Log($"SyncGroup: {Convert.ToString(bitMask, 2).PadLeft(16, '0')}, ExecuteFlags: {Convert.ToString((ushort)result, 2).PadLeft(16, '0')}");
            return result;
        }
    }
}