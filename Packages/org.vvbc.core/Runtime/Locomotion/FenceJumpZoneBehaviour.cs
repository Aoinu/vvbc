using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

namespace VVBC.Locomotion
{
    // A fence jump zone (docs/development/player-perks.md, Jump): a trigger collider on this
    // GameObject, placed and sized in the scene, where a player with the Jump I perk jumps to
    // 1.30 m. It only tells the scene's Locomotion that the local player entered or left; what
    // the jump becomes is decided by whoever sets Locomotion's fence impulse (PerkEffects). Local
    // only, event driven (no per-frame work), any number of zones (overlaps are counted). The
    // collider is a trigger: ground, fences and batted balls are not affected.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public partial class FenceJumpZoneBehaviour : TsukimiBehaviour
    {
        [Tooltip("The scene's Locomotion.")]
        public LocomotionBehaviour locomotion;

        // This zone's own count of the local player, so an exit without an enter (a teleport or
        // a zone disabled while inside) never counts twice.
        private bool _inside;

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal) return;
            Enter();
        }

        public override void OnPlayerTriggerExit(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal) return;
            Exit();
        }

        // A zone disabled (or destroyed) with the player inside lets go of its count.
        private void OnDisable()
        {
            Exit();
        }

        public bool IsInside() { return _inside; }

        public void Enter()
        {
            if (_inside || locomotion == null) return;
            _inside = true;
            locomotion.EnterFenceZone();
        }

        public void Exit()
        {
            if (!_inside) return;
            _inside = false;
            if (locomotion != null) locomotion.ExitFenceZone();
        }
    }
}
