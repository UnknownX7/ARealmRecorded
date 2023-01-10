using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace ARealmRecorded.Structures;

[StructLayout(LayoutKind.Explicit, Size = 0x720)]
public unsafe struct FFXIVReplay
{
    [StructLayout(LayoutKind.Explicit, Size = 0x60)]
    public struct Header
    {
        private static readonly byte[] validBytes = { 0x46, 0x46, 0x58, 0x49, 0x56, 0x52, 0x45, 0x50, 0x4C, 0x41, 0x59 };

        [FieldOffset(0x0)] public fixed byte FFXIVREPLAY[12]; // FFXIVREPLAY
        [FieldOffset(0xC)] public short u0xC; // Always 4? Possibly replay system version, wont play if not 4
        [FieldOffset(0xE)] public short u0xE; // Always 3? Seems to be unused
        [FieldOffset(0x10)] public int replayVersion; // Has to match the version of the replay system
        [FieldOffset(0x14)] public uint timestamp; // Unix timestamp
        [FieldOffset(0x18)] public uint u0x18; // # Bytes? Seems to be unused
        [FieldOffset(0x1C)] public uint ms;
        [FieldOffset(0x20)] public ushort contentID;
        // 0x22-0x28 Padding? Does not appear to be used
        [FieldOffset(0x28)] public byte info; // Bitfield, 1 = Up to date, 2 = Locked, 4 = Duty completed
        // 0x29-0x30 Padding? Does not appear to be used
        [FieldOffset(0x30)] public ulong localCID; // ID of the recorder (Has to match the logged in character)
        [FieldOffset(0x38)] public fixed byte jobs[8]; // Job ID of each player
        [FieldOffset(0x40)] public byte playerIndex; // The index of the recorder in the jobs array
        [FieldOffset(0x44)] public int u0x44; // Always 772? Seems to be unused
        [FieldOffset(0x48)] public int replayLength; // Number of bytes in the replay
        [FieldOffset(0x4C)] public short u0x4C; // Padding? Seems to be unused
        [FieldOffset(0x4E)] public fixed ushort npcNames[7]; // Determines displayed names using the BNpcName sheet
        [FieldOffset(0x5C)] public int u0x5C; // Probably just padding

        public bool IsValid
        {
            get {
                for (int i = 0; i < validBytes.Length; i++)
                {
                    if (validBytes[i] != FFXIVREPLAY[i])
                        return false;
                }
                return true;
            }
        }

        public bool IsPlayable => replayVersion == Game.ffxivReplay->replayVersion && u0xC == 4;

        public bool IsLocked => IsValid && IsPlayable && (info & 2) != 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x4 + 0xC * 64)]
    public struct ChapterArray
    {
        [FieldOffset(0x0)] public int length;

        [StructLayout(LayoutKind.Sequential, Size = 0xC)]
        public struct Chapter
        {
            public int type; // 1 = Countdown, 2 = Start/Restart, 3 = ???, 4 = Event Cutscene, 5 = Barrier down (displayed as Start/Restart)
            public uint offset; // byte offset
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
                    return (Chapter*)((nint)ptr + 4) + i;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x68)]
    public struct InitZonePacket
    {
        [FieldOffset(0x0)] public ushort u0x0;
        [FieldOffset(0x2)] public ushort territoryType; // Used to determine if you can play a recording (possibly as well as if it can be recorded?)
        [FieldOffset(0x4)] public ushort u0x4;
        [FieldOffset(0x6)] public ushort contentFinderCondition; // Stops recording if 0
    }

    [StructLayout(LayoutKind.Explicit, Size = 0xC0)]
    public struct UnknownPacket
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ReplayDataSegment
    {
        public ushort opcode;
        public ushort dataLength;
        public uint ms;
        public uint objectID;

        public uint Length => (uint)sizeof(ReplayDataSegment) + dataLength;

        public byte* Data
        {
            get
            {
                fixed (void* ptr = &this)
                    return (byte*)ptr + sizeof(ReplayDataSegment);
            }
        }
    }

    // .dat Info
    // 0x364 is the start of the replay data, everything before this is the Header + ChapterArray
    [StructLayout(LayoutKind.Sequential)]
    public struct ReplayFile
    {
        public Header header;
        public ChapterArray chapters;

        public byte* Data
        {
            get
            {
                fixed (void* ptr = &this)
                    return (byte*)ptr + sizeof(Header) + sizeof(ChapterArray);
            }
        }

        public ReplayDataSegment* GetDataSegment(uint offset) => offset < header.replayLength ? (ReplayDataSegment*)((long)Data + offset) : null;
    }

    [FieldOffset(0x0)] public int replayVersion; // No idea if this is the version of the game or the version of the replay system
    [FieldOffset(0x8)] public nint fileStream; // Start of save/read area
    [FieldOffset(0x10)] public nint fileStreamNextWrite; // Next area to be written to while recording
    [FieldOffset(0x18)] public nint fileStreamEnd; // End of save/read area
    [FieldOffset(0x20)] public long u0x20;
    [FieldOffset(0x28)] public long u0x28;
    [FieldOffset(0x30)] public long dataOffset; // Next? offset of bytes to read from the save/read area (up to 1MB)
    [FieldOffset(0x38)] public long overallDataOffset; // Overall (next?) offset of bytes to read
    [FieldOffset(0x40)] public long lastDataOffset; // Last? offset read
    [FieldOffset(0x48)] public Header replayHeader;
    [FieldOffset(0xA8)] public ChapterArray chapters; // ms of the first chapter determines the displayed time, but doesn't affect chapter times
    [FieldOffset(0x3B0)] public Utf8String contentTitle; // Current content name
    [FieldOffset(0x418)] public long nextDataSection; // 0x100000 if the lower half of the save/read area is next to be loaded into, 0x80000 if the upper half is
    [FieldOffset(0x420)] public long numberBytesRead; // How many bytes have been read from the file
    [FieldOffset(0x428)] public int currentFileSection; // Currently playing section starting at 1 (each section is 512 kb)
    [FieldOffset(0x42C)] public int dataLoadType; // 7 = Load header + chapters, 8 = Load section, 10 = Load header (3-6 and 11 are used for saving?)
    [FieldOffset(0x430)] public long dataLoadOffset; // Starting offset to load the next section into
    [FieldOffset(0x438)] public long dataLoadLength; // 0x100000 or replayLength if initially loading, 0x80000 afterwards
    [FieldOffset(0x440)] public long dataLoadFileOffset; // Offset to begin loading data from
    [FieldOffset(0x448)] public long localCID;
    [FieldOffset(0x450)] public byte currentReplaySlot; // 0-2 depending on which replay it is, 255 otherwise
    // 0x451-0x458 Padding?
    [FieldOffset(0x458)] public Utf8String characterRecordingName; // "<Character name> Duty Record #<Slot>" but only when recording, not when replaying
    [FieldOffset(0x4C0)] public Utf8String replayTitle; // contentTitle + the date and time, but only when recording, not when replaying
    [FieldOffset(0x528)] public Utf8String u0x528;
    [FieldOffset(0x590)] public float recordingTime; // Only used when recording
    [FieldOffset(0x598)] public long recordingLength; // Only used when recording
    [FieldOffset(0x5A0)] public int u0x5A0;
    [FieldOffset(0x5A4)] public byte u0x5A4;
    [FieldOffset(0x5A5)] public byte nextReplaySaveSlot;
    [FieldOffset(0x5A8)] public Header* savedReplayHeaders; // Pointer to the three saved replay headers
    [FieldOffset(0x5B0)] public nint u0x5B0; // Pointer right after the file headers
    [FieldOffset(0x5B8)] public nint u0x5B8; // Same as above?
    [FieldOffset(0x5C0)] public byte u0x5C0;
    [FieldOffset(0x5C4)] public uint localPlayerObjectID;
    [FieldOffset(0x5C8)] public InitZonePacket initZonePacket; // The last received InitZone is saved here
    [FieldOffset(0x630)] public long u0x630;
    [FieldOffset(0x638)] public UnknownPacket u0x638; // Probably a packet
    [FieldOffset(0x6F8)] public int u0x6F8;
    [FieldOffset(0x6FC)] public float seek; // Determines current time, but always seems to be slightly ahead
    [FieldOffset(0x700)] public float seekDelta; // Stores how far the seek moves per second
    [FieldOffset(0x704)] public float speed;
    [FieldOffset(0x708)] public float u0x708; // Seems to be 1 or 0, depending on if the speed is greater than 1 (Probably sound timescale)
    [FieldOffset(0x70C)] public byte selectedChapter; // 64 when playing, otherwise determines the current chapter being seeked to
    [FieldOffset(0x710)] public uint startingMS; // The ms considered 00:00:00
    [FieldOffset(0x714)] public int u0x714;
    [FieldOffset(0x718)] public short u0x718;
    [FieldOffset(0x71A)] public byte status; // Bitfield determining the current status of the system (1 Just logged in?, 2 Can record, 4 Saving packets, 8 ???, 16 Record Ready Checked?, 32 Save recording?, 64 Barrier down, 128 In playback after barrier drops?)
    [FieldOffset(0x71B)] public byte playbackControls; // Bitfield determining the current playback controls (1 Waiting to enter playback, 2 Waiting to leave playback?, 4 In playback (blocks packets), 8 Paused, 16 Chapter???, 32 Chapter???, 64 In duty?, 128 In playback???)
    [FieldOffset(0x71C)] public byte u0x71C; // Bitfield? (1 Used to apply the initial chapter the moment the barrier drops while recording)
    // 0x71D-0x720 is padding
}