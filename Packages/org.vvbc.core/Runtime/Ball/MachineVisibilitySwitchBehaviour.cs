using TMPro;
using Tsukimi;
using UnityEngine;

// The lane's SHOW / HIDE switch for the batting machine. Holds no state of its own: Interact asks
// the lane (HomePlateSet) to change, and the lane calls back whenever its synced state is applied.
// See docs/development/home-plate-set.md.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class MachineVisibilitySwitchBehaviour : TsukimiBehaviour
{
    public HomePlateSetBehaviour lane;
    [Tooltip("Shows what the machine is doing now: HIDDEN, STOPPED, RUNNING or LOCAL MODE.")]
    public TextMeshPro stateText;
    [Tooltip("Shows what pressing does: SHOW or HIDE.")]
    public TextMeshPro actionText;
    [Tooltip("Coloured to match the state.")]
    public Renderer lamp;
    public Color hiddenColor = new Color(0.36f, 0.42f, 0.5f, 0.5f);
    public Color stoppedColor = new Color(0.157f, 0.808f, 0.894f, 0.5f);
    public Color runningColor = new Color(0.18f, 0.74f, 0.42f, 0.5f);

    private MaterialPropertyBlock _block;

    private void Start()
    {
        _OnMachineStateChanged();
    }

    public override void Interact()
    {
        if (lane != null) lane.ToggleMachineVisibility();
    }

    // Called by the lane after it applied its state on this client.
    public void _OnMachineStateChanged()
    {
        if (lane == null) return;
        int state = lane.MachineState();
        bool shown = state != HomePlateSetBehaviour.MachineHidden;
        bool running = state == HomePlateSetBehaviour.MachineOn;
        bool local = lane.IsLocalMode();
        if (stateText != null)
            stateText.text = !shown ? "HIDDEN" : local ? "LOCAL MODE" : running ? "RUNNING" : "STOPPED";
        if (actionText != null) actionText.text = shown ? "HIDE" : "SHOW";
        InteractionText = shown ? "Hide Batting Machine" : "Show Batting Machine";
        if (lamp != null)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            lamp.GetPropertyBlock(_block);
            _block.SetColor("_Color", !shown ? hiddenColor : running ? runningColor : stoppedColor);
            lamp.SetPropertyBlock(_block);
        }
    }
}
