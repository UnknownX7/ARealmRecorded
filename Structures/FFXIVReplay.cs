using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace ARealmRecorded.Structures;

[StructLayout(LayoutKind.Explicit, Size = 0x718)]
public unsafe struct FFXIVReplay
{
    [StructLayout(LayoutKind.Explicit, Size = 0x60)]
    public struct Header
    {
        [FieldOffset(0x0)] public fixed byte FFXIVREPLAY[12]; // FFXIVREPLAY
        [FieldOffset(0xC)] public short u0xC; // Always 4? Possibly replay system version, wont play if not 4
        [FieldOffset(0xE)] public short u0xE; // Always 3? Seems to be unused
        [FieldOffset(0x10)] public int replayVersion; // Has to match the version of the replay system
        [FieldOffset(0x14)] public uint timestamp; // Unix timestamp
        [FieldOffset(0x18)] public uint u0x18; // # Bytes? Seems to be unused
        [FieldOffset(0x1C)] public uint ms;
        [FieldOffset(0x20)] public ushort contentID;
        // 0x22-0x28 Padding? Does not appear to be used
        [FieldOffset(0x28)] public byte info; // Bitfield, 0b001 = Up to date, 0b010 = Locked, 0b100 = Duty completed
        // 0x29-0x30 Padding? Does not appear to be used
        [FieldOffset(0x30)] public ulong localCID; // ID of the recorder (Has to match the logged in character)
        [FieldOffset(0x38)] public fixed byte jobs[8]; // Job ID of each player
        [FieldOffset(0x40)] public byte playerIndex; // The index of the recorder in the jobs array
        [FieldOffset(0x44)] public int u0x44; // Always 772? Seems to be unused
        [FieldOffset(0x48)] public int replayLength; // Number of bytes in the replay
        [FieldOffset(0x4C)] public short u0x4C; // Padding? Seems to be unused
        [FieldOffset(0x4E)] public fixed ushort npcNames[7]; // Determines displayed names using the BNpcName sheet
        [FieldOffset(0x5C)] public int u0x5C; // Probably just padding
    }

    [StructLayout(LayoutKind.Explicit, Size = 0xC * 64)]
    public struct ChapterArray
    {
        [FieldOffset(0x0)] public int length;

        [StructLayout(LayoutKind.Sequential, Size = 0xC)]
        public struct Chapter
        {
            public int type; // 1 = Countdown, 2 = Start/Restart, 3 = ???, 4 = Event Cutscene, 5 = Barrier down (displayed as Start/Restart)
            public uint offset; // byte offset?
            public uint ms; // ms from the start of the instance
        }

        public Chapter* this[int i]
        {
            get
            {
                if (i is < 0 or > 63)
                    return null;

                fixed (void* ptr = &this)
                {
                    return (Chapter*)((IntPtr)ptr + 4) + i;
                }
            }
        }
    }

    // .dat Info
    // 0x364 is the start of the replay data, everything before this is the Header + ChapterArray
    [StructLayout(LayoutKind.Sequential)]
    public struct ReplayDataSegment
    {
        public ushort opcode;
        public ushort dataLength;
        public uint ms;
        public uint objectID;

        public byte* Data
        {
            get
            {
                fixed (void* ptr = &this)
                {
                    return (byte*)ptr + 0xC;
                }
            }
        }
    }

    [FieldOffset(0x0)] public int replayVersion; // No idea if this is the version of the game or the version of the replay system
    [FieldOffset(0x8)] public IntPtr fileStream; // Start of save/read area
    [FieldOffset(0x10)] public IntPtr fileStreamNextWrite; // Next area to be written to while recording
    [FieldOffset(0x18)] public IntPtr fileStreamEnd; // End of save/read area
    // 0x20-0x30 Padding?
    [FieldOffset(0x30)] public long dataOffset; // Next? offset of bytes to read from the save/read area (up to 1MB)
    [FieldOffset(0x38)] public long overallDataOffset; // Overall (next?) offset of bytes to read
    [FieldOffset(0x40)] public long lastDataOffset; // Last? offset read
    [FieldOffset(0x48)] public Header replayHeader;
    [FieldOffset(0xA8)] public ChapterArray chapters; // ms of the first chapter determines the displayed time, but doesn't affect chapter times
    [FieldOffset(0x3B0)] public Utf8String contentTitle; // Current content name
    [FieldOffset(0x418)] public long nextDataSection; // 0x100000 if the lower half of the save/read area is next to be loaded into, 0x80000 if the upper half is
    [FieldOffset(0x420)] public long numberBytesRead; // How many bytes have been read from the file
    [FieldOffset(0x428)] public int currentFileSection; // Currently playing section starting at 1 (each section is 512 kb)
    [FieldOffset(0x42C)] public int dataLoadType; // 7 = Load header + chapters, 8 = Load section, 10 = Load header
    [FieldOffset(0x430)] public long dataLoadOffset; // Starting offset to load the next section into
    [FieldOffset(0x438)] public long dataLoadLength; // 0x100000 or replayLength if initially loading, 0x80000 afterwards
    [FieldOffset(0x440)] public long dataLoadFileOffset; // Offset to begin loading data from
    [FieldOffset(0x448)] public long localCID;
    [FieldOffset(0x450)] public byte currentReplaySlot; // 0-2 depending on which replay it is, 255 otherwise
    // 0x451-0x458 Padding?
    [FieldOffset(0x458)] public Utf8String characterRecordingName; // "<Character name> Duty Record #<Slot>" but only when recording, not when replaying
    [FieldOffset(0x4C0)] public Utf8String replayTitle; // contentTitle + the date and time, but only when recording, not when replaying
    [FieldOffset(0x528)] public Utf8String u0x528;
    [FieldOffset(0x590)] public int u0x590;
    [FieldOffset(0x598)] public long u0x598;
    [FieldOffset(0x5A0)] public int u0x5A0;
    [FieldOffset(0x5A4)] public byte u0x5A4;
    [FieldOffset(0x5A5)] public byte nextReplaySaveSlot;
    [FieldOffset(0x5A8)] public Header* savedReplayHeaders; // Pointer to the three saved replay headers
    [FieldOffset(0x5B0)] public IntPtr u0x5B0; // Pointer right after the file headers
    [FieldOffset(0x5B8)] public IntPtr u0x5B8; // Same as above?
    [FieldOffset(0x5C0)] public byte u0x5C0;
    [FieldOffset(0x5C4)] public uint localPlayerObjectID;
    [FieldOffset(0x5C8)] public ushort u0x5C8; // Seems to be the start of something copied from somewhere else, with a size of 0x60 (96)
    [FieldOffset(0x5CA)] public ushort currentTerritoryType; // TerritoryType of your actual ingame location, used to determine if you can play a recording (possibly as well as if it can be recorded?)
    // 0x628, end of whatever this is
    [FieldOffset(0x628)] public long u0x628;
    [FieldOffset(0x630)] public long u0x630; // Seems to be the start of something copied from somewhere else, with a size of 0xC0 (192)
    // 0x6F0, end of whatever this is
    [FieldOffset(0x6F0)] public int u0x6F0;
    [FieldOffset(0x6F4)] public float seek; // Determines current time, but always seems to be slightly ahead
    [FieldOffset(0x6F8)] public float u0x6F8; // Seems to be 1 or 0, depending on if the recording is currently playing, jumps to high values while seeking a chapter
    [FieldOffset(0x6FC)] public float speed;
    [FieldOffset(0x700)] public float u0x700; // Seems to be 1 or 0, depending on if the speed is greater than 1 (Probably sound timescale)
    [FieldOffset(0x704)] public byte selectedChapter; // 64 when playing, otherwise determines the current chapter being seeked to
    [FieldOffset(0x708)] public uint startingMS; // The ms considered 00:00:00
    [FieldOffset(0x70C)] public int u0x70C;
    [FieldOffset(0x710)] public short u0x710;
    [FieldOffset(0x712)] public byte status; // Bitfield determining the current status of the system (1 Just logged in?, 2 Can record, 4 ???, 8 ???, 16 Recording?, 32 Recording?, 64 Barrier down, 128 In playback after duty begins?)
    [FieldOffset(0x713)] public byte playbackControls; // Bitfield determining the current playback controls (1 Waiting to enter playback, 2 ???, 4 In playback (blocks packets), 8 Paused, 16 Chapter???, 32 Chapter???, 64 In duty?, 128 In playback???)
    [FieldOffset(0x714)] public byte u0x714;
    // 0x715-0x718 is padding
}