using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Memory;
using Hypostasis.Game.Structures;

namespace ARealmRecorded;

public abstract unsafe class CustomReplayPacket
{
    public abstract ushort Opcode { get; }

    protected void Write(uint objectID, byte[] data)
    {
        if (Common.ContentsReplayModule->IsRecording)
            Common.ContentsReplayModule->WritePacket(objectID, Opcode, data);
        else
            ReplayPacketManager.WriteBuffer(objectID, Opcode, data);
    }

    public abstract void Replay(FFXIVReplay.DataSegment* segment, byte* data);
}

public unsafe class RSVPacket : CustomReplayPacket
{
    public override ushort Opcode => 0xF001;

    private delegate Bool RsvReceiveDelegate(byte* data);
    [HypostasisSignatureInjection("44 8B 09 4C 8D 41 34", Required = true)]
    private static Hook<RsvReceiveDelegate> RsvReceiveHook;
    private Bool RsvReceiveDetour(byte* data)
    {
        var size = *(int*)data; // Value size
        var length = size + 0x4 + 0x30; // Package size
        Write(0xE000_0000, MemoryHelper.ReadRaw((nint)data, length));
        return RsvReceiveHook.Original(data);
    }

    public override void Replay(FFXIVReplay.DataSegment* segment, byte* data) => RsvReceiveHook.Original(data);
}

public unsafe class RSFPacket : CustomReplayPacket
{
    public override ushort Opcode => 0xF002;

    private delegate Bool RsfReceiveDelegate(byte* data);
    [HypostasisSignatureInjection("48 8B 11 4C 8D 41 08", Required = true)]
    private static Hook<RsfReceiveDelegate> RsfReceiveHook;
    private Bool RsfReceiveDetour(byte* data)
    {
        Write(0xE000_0000, MemoryHelper.ReadRaw((nint)data, 0x48));
        return RsfReceiveHook.Original(data);
    }

    public override void Replay(FFXIVReplay.DataSegment* segment, byte* data) => RsfReceiveHook.Original(data);
}

public static unsafe class ReplayPacketManager
{
    public static Dictionary<uint, CustomReplayPacket> CustomPackets { get; set; } = new();
    private static List<(uint, ushort, byte[])> buffer = new();

    public static void Initialize()
    {
        foreach (var t in Util.Assembly.GetTypes<CustomReplayPacket>())
        {
            try
            {
                var packet = (CustomReplayPacket)Activator.CreateInstance(t);
                if (packet == null) continue;

                DalamudApi.SigScanner.Inject(packet);
                CustomPackets.Add(packet.Opcode, packet);
            }
            catch (Exception e)
            {
                DalamudApi.LogError($"Failed to initialize custom packet handler {t}", e);
            }
        }
    }

    public static bool ReplayPacket(FFXIVReplay.DataSegment* segment, byte* data)
    {
        if (!CustomPackets.TryGetValue(segment->opcode, out var packet)) return false;
        //DalamudApi.LogDebug($"Replaying Custom Packet: 0x{segment->opcode:X}");
        packet.Replay(segment, data);
        return true;
    }

    public static void WriteBuffer(uint objectID, ushort opcode, byte[] data)
    {
        buffer.Add((objectID, opcode, data));
        if (buffer.Count == 1)
            DalamudApi.Framework.RunOnTick(buffer.Clear, new TimeSpan(0, 0, 10));
    }

    public static void FlushBuffer()
    {
        if (Common.ContentsReplayModule->IsSavingPackets && buffer.Count > 0)
        {
            //DalamudApi.LogDebug($"Recording {buffer.Count} packets");
            foreach (var (objectID, opcode, data) in buffer)
            {
                //DalamudApi.LogDebug($"{CustomPackets[opcode].GetType()}, Length: {data.Length}");
                Common.ContentsReplayModule->WritePacket(objectID, opcode, data);
            }
        }

        buffer.Clear();
    }
}