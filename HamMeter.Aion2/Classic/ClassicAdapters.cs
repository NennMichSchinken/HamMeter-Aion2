using AionDpsMeter.Core.GameData.Services;
using AionDpsMeter.Core.Models;
using AionDpsMeter.Services.PacketCapture;
using AionDpsMeter.Services.Services.Entity;
using HamMeter.Capture;
using HamMeter.Game;

namespace HamMeter.Classic;

// Kuroukihime's entity tracking seen through HamMeter's IEntityDirectory.
public sealed class ClassicEntities(EntityTracker tracker) : IEntityDirectory
{
    public event Action<int, int>? SummonRegistered
    {
        add => tracker.SummonRegistered += value;
        remove => tracker.SummonRegistered -= value;
    }

    public int? SummonOwner(int entityId) => tracker.GetSummonOwner(entityId);

    public bool IsSummon(int entityId) => tracker.IsSummon(entityId);

    public KnownPlayer? Player(int entityId) =>
        tracker.GetPlayerEntity(entityId) is { } p ? ToKnown(p) : null;

    public KnownPlayer AddPlayer(int entityId, long classId) =>
        ToKnown(tracker.GetOrCreateSessionPlayer(entityId, GameDataProvider.Instance.Classes.GetById((int)classId)));

    public string TargetName(int entityId) =>
        tracker.GetTargetMob(entityId) is { MobCode: not 0 } mob ? mob.Name : string.Empty;

    public bool IsBoss(int entityId) => tracker.GetTargetMob(entityId) is { MobCode: not 0, IsBoss: true };

    private static KnownPlayer ToKnown(Player p) =>
        new(p.Id, p.IsIdentified ? p.Name : null, p.CharacterClass?.Id ?? 0, p.IsUser);
}

// Kuroukihime's game data seen through HamMeter's ISkillRules.
public sealed class ClassicSkillRules : ISkillRules
{
    private readonly GameDataProvider m_data = GameDataProvider.Instance;

    public long? ClassOf(int skillCode) => m_data.GetClassBySkillCode(skillCode)?.Id;

    public bool IsTheostone(int skillCode) => m_data.IsTheostone(skillCode);

    public bool IsHealing(int skillCode) => m_data.IsHealingSkill(skillCode);

    // Only their curated DoT skills count as damage ticks.
    public bool CountsAsTickDamage(int skillCode) => m_data.IsDotDamageSkill(skillCode) && !m_data.IsHealingSkill(skillCode);
}

// Feeds captured payload into their TcpStreamBuffer.
public sealed class TcpStreamBufferSink(TcpStreamBuffer buffer) : IStreamSink
{
    public void AddData(string streamKey, byte[] payload, long receivedAtUnixMs) => buffer.AddData(streamKey, payload, receivedAtUnixMs);

    public void ClearStream(string streamKey) => buffer.ClearStream(streamKey);
}

// HamMeter's raw-socket capture as their IPacketCaptureDevice.
public sealed class ClassicCaptureAdapter(IGameCapture inner) : IPacketCaptureDevice
{
    public IGameCapture Inner => inner;

    public bool IsCapturing => inner.IsCapturing;

    public string? DeviceName => inner.DeviceName;

    public void StartCapture() => inner.StartCapture();

    public void StopCapture() => inner.StopCapture();

    public void DiscoverAdapters()
    {
        // Interfaces are chosen from Aion's own connections at runtime.
    }

    public void Dispose() => inner.Dispose();
}
