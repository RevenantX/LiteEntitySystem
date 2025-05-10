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

        public void SetGroupEnabled(SyncGroup group, bool enabled) =>
            EnabledGroups = enabled 
                ? EnabledGroups | group
                : EnabledGroups & ~group;
    }

    public static class SyncGroupUtils
    {
        public static SyncFlags ToSyncFlags(SyncGroup sv)
        {
            int bitMask = (int)sv & 0x1F; //first 5 bits
            SyncFlags result = bitMask == 0 
                ? SyncFlags.None
                : (SyncFlags)(bitMask << 6); //because SyncFlags.SyncGroup1 is 1 << 6
            //Logger.Log($"SyncGroup: {Convert.ToString(bitMask, 2).PadLeft(16, '0')}, SyncFlags: {Convert.ToString((ushort)result, 2).PadLeft(16, '0')}");
            return result;
        }
        
        public static bool IsSyncVarDisabled(SyncGroup sv, SyncFlags flags)
        {
            int disabledMask = ~(int)sv & 0x1F; //first 5 bits
            int syncFlagsMask = ((int)flags >> 6) & 0x1F; //shift right and cut to 5 bits too
            if (syncFlagsMask == 0)
                return false;
            
            bool isDisabled = (disabledMask & syncFlagsMask) != 0;
            //Logger.Log($"IsSyncVarDisabled: {Convert.ToString(disabledMask, 2).PadLeft(16, '0')}, SyncFlags: {Convert.ToString(syncFlagsMask, 2).PadLeft(16, '0')}: {isDisabled}");
            return isDisabled;
        }

        public static bool IsRPCDisabled(SyncGroup sv, ExecuteFlags flags)
        {
            int disabledMask = ~(int)sv & 0x1F; //first 5 bits
            int executeFlagsMask = ((int)flags >> 4) & 0x1F; //shift right and cut to 5 bits too
            if (executeFlagsMask == 0)
                return false;
            
            bool isDisabled = (disabledMask & executeFlagsMask) != 0;
            //Logger.Log($"IsRPCDisabled: {Convert.ToString(disabledMask, 2).PadLeft(16, '0')}, ExecuteFlags: {Convert.ToString(executeFlagsMask, 2).PadLeft(16, '0')}: {isDisabled}");
            return isDisabled;
        }
    }
}