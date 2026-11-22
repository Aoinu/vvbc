using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

namespace VVBC.Locomotion
{
    // Entry point of the Station locomotion validation: a trigger covering the field. The
    // local player's trigger entry asks that player's LocomotionController to enter its
    // Station. It never releases the Station (no exit handling, no monitoring): the player
    // leaves it by Respawn. Every spawn point must lie outside this trigger.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public partial class FieldLocomotionZoneBehaviour : TsukimiBehaviour
    {
        [Tooltip("Retries when the local PlayerObject is not created yet at the entry.")]
        [SerializeField] private int maxResolveRetries = 10;
        [SerializeField] private int retryIntervalFrames = 15;

        private LocomotionControllerBehaviour _controller;
        private int _retries;
        private bool _retryPending;

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal) return;
            if (_retryPending) return;
            _retries = 0;
            TryAssign();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (Utilities.IsValid(player) && player.isLocal) WarnIfInside(player, "join");
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (Utilities.IsValid(player) && player.isLocal) WarnIfInside(player, "respawn");
        }

        public void _RetryAssign()
        {
            if (!_retryPending) return;
            _retryPending = false;
            TryAssign();
        }

        private void TryAssign()
        {
            LocomotionControllerBehaviour controller = ResolveController();
            if (controller == null)
            {
                if (_retries >= maxResolveRetries)
                {
                    Debug.LogWarning("[VVBC Locomotion] local LocomotionPlayerObject not found; Station not assigned");
                    return;
                }
                _retries++;
                _retryPending = true;
                SendCustomEventDelayedFrames(nameof(_RetryAssign), retryIntervalFrames);
                return;
            }
            controller.RequestEnter();
        }

        // The local player's own controller among its PlayerObjects, cached once found.
        private LocomotionControllerBehaviour ResolveController()
        {
            if (_controller != null) return _controller;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) return null;
            GameObject[] objects = Networking.GetPlayerObjects(player);
            if (objects == null) return null;
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null) continue;
                LocomotionControllerBehaviour candidate = objects[i].GetComponentInChildren<LocomotionControllerBehaviour>();
                if (candidate == null || !Networking.IsOwner(player, candidate.gameObject)) continue;
                _controller = candidate;
                return candidate;
            }
            return null;
        }

        // Configuration check only: a spawn inside the field would re-enter the Station
        // right after Respawn, which this design does not support.
        private void WarnIfInside(VRCPlayerApi player, string when)
        {
            Collider zone = GetComponent<Collider>();
            if (zone == null || !zone.bounds.Contains(player.GetPosition())) return;
            Debug.LogWarning("[VVBC Locomotion] player is inside FieldLocomotionZone at " + when
                + "; every spawn point must be outside it");
        }
    }
}
