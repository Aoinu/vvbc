using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class BattingPitcherBehaviour
{
    // Prepares the next item the way BeginPreparation does and returns its properties.
    private PitchProperties PrepareNext()
    {
        int index = ChooseNextIndex();
        PitchProperties properties = PreparedProperties(index < 0 ? FallbackItem : _playlist[index],
            index < 0 ? PitchPlaylist.DefaultBuild : _playlistBuilds[index]);
        _reservedItemId = index < 0 ? 0 : PitchPlaylist.Id(_playlist[index]);
        MarkReservedItem();
        return properties;
    }

    [TsukimiTest]
    public void EachPreparedPitchUsesItsOwnItemBuildAndTheBaseSettings()
    {
        Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        SubmitSettings(140f, 2600f, 3f, false);
        PitchProperties fallback = PrepareNext();
        Assert.AreEqual(140f / 3.6f, fallback.speedMps, "the empty list pitches the base 4-Seam");
        Assert.AreEqual(PitchType.FourSeam, selectedPitchType);
        int slow = PitchPlaylist.PackBuild(PitchType.Slider, 0f, 1f, 1f);
        int fast = PitchPlaylist.PackBuild(PitchType.Slider, 1f, 0f, 0f);
        AddBuild(PitchType.Slider, 16, slow);
        AddBuild(PitchType.Slider, 16, fast);
        PitchProperties first = PrepareNext();
        PitchProperties second = PrepareNext();
        Assert.AreEqual(PitchPropertiesCalculator.Calculate(PitchPlaylist.Build(PitchPlaylist.Pack(1, PitchType.Slider, 16), slow),
            140f / 3.6f, 2600f).speedMps, first.speedMps);
        Assert.AreEqual(second.speedMps, _speedSnapshot, "the release speed is the prepared pitch's");
        Assert.IsTrue(second.speedMps > first.speedMps, "the fast Slider build is faster");
        Assert.AreNotEqual(first.spinAxisLocal, second.spinAxisLocal, "and spins differently");
        Assert.AreEqual(PitchType.Slider, selectedPitchType);
    }

    [TsukimiTest]
    public void AKnuckleGetsARotatingWakeWithANewPhaseForEveryPitch()
    {
        Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        int knuckle = AddBuild(PitchType.Knuckle, 16, PitchPlaylist.PackBuild(PitchType.Knuckle, 0.5f, 0.5f, 0.5f));
        PitchProperties first = PrepareNext();
        WakeProfile snapshot = _wakeSnapshot;
        Assert.AreEqual(BallAerodynamics.WakeRotating, snapshot.mode);
        Assert.AreEqual(first.wakeLocal.initialPhase, snapshot.initialPhase);
        // the snapshot is fixed: editing the item afterwards does not reach the prepared pitch
        RequestSaveItem(knuckle, PitchType.Knuckle, 16, PitchPlaylist.PackBuild(PitchType.Knuckle, 1f, 0f, 1f));
        Assert.AreEqual(snapshot.coneAngle, _wakeSnapshot.coneAngle);
        Assert.AreEqual(snapshot.direction, _wakeSnapshot.direction);
        PitchProperties second = PrepareNext();
        Assert.AreNotEqual(first.wakeLocal.initialPhase, second.wakeLocal.initialPhase, "a new phase per pitch");
        Assert.IsTrue(second.wakeLocal.coneAngle > first.wakeLocal.coneAngle, "the next pitch uses the edited build");
    }

    [TsukimiTest]
    public void OnlyARotatingWakeDrawsAPhaseAndNeverFromTheShuffleSeed()
    {
        _phaseSeed = 1000;
        _shuffleSeed = 2000;
        int knuckle = PitchPlaylist.Pack(1, PitchType.Knuckle, 16);
        for (int type = 0; type < PitchType.Count; type++)
        {
            if (type == PitchType.Knuckle) continue;
            PitchProperties p = PreparedProperties(PitchPlaylist.Pack(1, type, 16), PitchPlaylist.DefaultBuild);
            Assert.AreEqual(0f, p.wakeLocal.initialPhase);
        }
        Assert.AreEqual(1000, _phaseSeed, "other types leave the phase seed alone");
        PitchProperties first = PreparedProperties(knuckle, PitchPlaylist.DefaultBuild);
        Assert.AreEqual(PitchPropertiesCalculator.PhaseFromSeed(PitchPlaylist.NextSeed(1000)), first.wakeLocal.initialPhase,
            "the same seed gives the same phase");
        Assert.AreEqual(first.wakeLocal.initialPhase, _wakeSnapshot.initialPhase, "the phase is in the snapshot");
        PitchProperties second = PreparedProperties(knuckle, PitchPlaylist.DefaultBuild);
        Assert.AreNotEqual(first.wakeLocal.initialPhase, second.wakeLocal.initialPhase, "one draw per pitch");
        Assert.AreEqual(2000, _shuffleSeed, "preparing a pitch never touches the shuffle seed");
    }

    // The playlist order depends only on the shuffle seed and the item positions: the same lap
    // sequence comes out whether or not the list holds Knuckles.
    [TsukimiTest]
    public void KnucklesDoNotChangeTheRandomPlaylistOrder()
    {
        Mimic.Join();
        _stateKnown = true;
        int[] withKnuckles = RandomLapRanks(new int[] { PitchType.Knuckle, PitchType.FourSeam, PitchType.Knuckle, PitchType.Slider });
        for (int i = 0; i < 4; i++) RequestDeleteItem(PitchPlaylist.Id(PlaylistItem(0)));
        int[] without = RandomLapRanks(new int[] { PitchType.FourSeam, PitchType.FourSeam, PitchType.Curve, PitchType.Slider });
        for (int i = 0; i < withKnuckles.Length; i++) Assert.AreEqual(without[i], withKnuckles[i], "pitch " + i);
    }

    // Adds the types in order and prepares three laps from one shuffle seed; returns the add
    // position of each prepared item.
    private int[] RandomLapRanks(int[] types)
    {
        int[] ids = new int[types.Length];
        for (int i = 0; i < types.Length; i++) ids[i] = AddItem(types[i], 16);
        _shuffleSeed = 777;
        int[] ranks = new int[types.Length * 3];
        for (int n = 0; n < ranks.Length; n++)
        {
            PrepareNext();
            for (int i = 0; i < ids.Length; i++) if (ids[i] == _reservedItemId) ranks[n] = i;
        }
        return ranks;
    }

    [TsukimiTest]
    public void ANewOwnerDrawsKnucklePhasesFromItsOwnSeed()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        AddBuild(PitchType.Knuckle, 16, PitchPlaylist.DefaultBuild);
        float before = PrepareNext().wakeLocal.initialPhase;
        Mimic.Deliver();
        Mimic.Become(other);
        RequestRunning(true);
        Assert.IsTrue(Networking.IsOwner(other, gameObject), "editing took Ownership");
        _phaseSeed = 424242;
        PitchProperties after = PrepareNext();
        Assert.AreEqual(BallAerodynamics.WakeRotating, after.wakeLocal.mode);
        Assert.AreEqual(PitchPropertiesCalculator.PhaseFromSeed(PitchPlaylist.NextSeed(424242)), after.wakeLocal.initialPhase);
        Assert.IsTrue(after.wakeLocal.initialPhase >= 0f && after.wakeLocal.initialPhase < 2f * Mathf.PI);
        Assert.AreNotEqual(before, after.wakeLocal.initialPhase);
    }

    // The synced schedule fields (declared [UdonSynced]) are what every client copies into the
    // pitch it plays; the release itself reads the server time and is covered in play mode.
    [TsukimiTest]
    public void TheAcceptedScheduleCarriesItsWakeAndIsTakenOnce()
    {
        _pitchId = 1;
        _startTime = 5.0;
        _releaseWakeMode = BallAerodynamics.WakeRotating;
        _releaseWakeCoefficient = 0.04f;
        _releaseWakeDirection = Vector3.forward;
        _releaseWakeConeAngle = 1.5f;
        _releaseWakePhase = 0.25f;
        Assert.IsTrue(AcceptPitch(1f));
        Assert.AreEqual(1, _appliedId);
        Assert.AreEqual(BallAerodynamics.WakeRotating, _appliedWake.mode);
        Assert.AreEqual(0.04f, _appliedWake.coefficient);
        Assert.AreEqual(Vector3.forward, _appliedWake.direction);
        Assert.AreEqual(1.5f, _appliedWake.coneAngle);
        Assert.AreEqual(0.25f, _appliedWake.initialPhase);
        Assert.IsFalse(AcceptPitch(0f), "the same schedule is not taken twice");
        _releaseWakeMode = BallAerodynamics.WakeNone;
        Assert.IsTrue(AcceptPitch(1f));
        Assert.AreEqual(BallAerodynamics.WakeNone, _appliedWake.mode, "a newer schedule replaces the wake");
    }
}
