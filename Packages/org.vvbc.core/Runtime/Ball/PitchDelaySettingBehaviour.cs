using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// The world's allowed sync delay for manual pitches (docs/development/manual-pitch-sync.md):
// one synced value, 0..500 ms on a 10 ms grid, 100 ms by default. Anyone may change it; the
// change takes this GameObject's ownership and is sent to everyone. It only holds the value:
// panels display and change it through Step/SetThreshold and are told of changes
// (OnPitchDelaySettingChanged). A batter reads it once per pitch, so a change during a pitch
// never changes that pitch's verdict. A LocalRig plate does not judge, so practice never
// depends on it.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class PitchDelaySettingBehaviour : TsukimiBehaviour
{
    [UdonSynced] public float thresholdMs = PitchJudgeMath.DefaultThresholdMs;

    private UdonBehaviour[] _listeners = new UdonBehaviour[0];

    public float ThresholdMs()
    {
        return PitchJudgeMath.ClampThreshold(thresholdMs);
    }

    public void Step(int steps)
    {
        SetThreshold(ThresholdMs() + steps * PitchJudgeMath.ThresholdStepMs);
    }

    public void SetThreshold(float value)
    {
        float next = PitchJudgeMath.ClampThreshold(value);
        if (next == ThresholdMs()) return;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;
        if (!Networking.IsOwner(local, gameObject)) Networking.SetOwner(local, gameObject);
        thresholdMs = next;
        if (Networking.IsOwner(local, gameObject)) RequestSerialization();
        NotifyListeners();
    }

    // A change that arrives while this client is taking ownership is sent once it owns it.
    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (Utilities.IsValid(player) && player.isLocal) RequestSerialization();
    }

    public override void OnDeserialization()
    {
        NotifyListeners();
    }

    public void RegisterListener(UdonBehaviour listener)
    {
        if (listener == null) return;
        for (int i = 0; i < _listeners.Length; i++)
            if (_listeners[i] == listener) return;
        UdonBehaviour[] next = new UdonBehaviour[_listeners.Length + 1];
        for (int i = 0; i < _listeners.Length; i++) next[i] = _listeners[i];
        next[next.Length - 1] = listener;
        _listeners = next;
    }

    private void NotifyListeners()
    {
        for (int i = 0; i < _listeners.Length; i++)
            if (_listeners[i] != null) _listeners[i].SendCustomEvent("OnPitchDelaySettingChanged");
    }
}
