using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

namespace VVBC.World
{
    // Initial world settings, applied once per join notification and never re-applied
    // later, so a future locomotion or ability system may change them freely. Movement
    // applies to the local player only; voice distances are this client's hearing of each
    // remote player (VRChat sends OnPlayerJoined for players already present as well).
    // Replaces the SDK sample VRCWorldSettings; keep only one of the two enabled.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public partial class WorldSettingsBehaviour : TsukimiBehaviour
    {
        [Header("Movement (local player, m/s)")]
        [Tooltip("Walk speed, in m/s. Default 2.")] public float walkSpeed = 2f;
        [Tooltip("Run speed, in m/s. Default 4.")] public float runSpeed = 4f;
        [Tooltip("Strafe speed, in m/s. Default 2.")] public float strafeSpeed = 2f;
        [Tooltip("Jump impulse, in m/s. Default 3.")] public float jumpImpulse = 3f;

        [Header("Voice (remote players, m)")]
        [Tooltip("Distance where voice falloff begins, in metres. Default 0.")] public float voiceDistanceNear = 0f;
        [Tooltip("Distance where voice becomes silent, in metres. Default 25.")] public float voiceDistanceFar = 25f;

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player)) return;
            if (player.isLocal) ApplyMovement(player);
            else ApplyVoice(player);
        }

        // A negative or non-finite value is not passed to the SDK.
        private void ApplyMovement(VRCPlayerApi player)
        {
            if (ValidAmount(walkSpeed)) player.SetWalkSpeed(walkSpeed);
            if (ValidAmount(runSpeed)) player.SetRunSpeed(runSpeed);
            if (ValidAmount(strafeSpeed)) player.SetStrafeSpeed(strafeSpeed);
            if (ValidAmount(jumpImpulse)) player.SetJumpImpulse(jumpImpulse);
        }

        // Both distances are left at the SDK values unless Near <= Far and both are valid.
        private void ApplyVoice(VRCPlayerApi player)
        {
            if (!VoiceDistancesValid(voiceDistanceNear, voiceDistanceFar)) return;
            player.SetVoiceDistanceNear(voiceDistanceNear);
            player.SetVoiceDistanceFar(voiceDistanceFar);
        }

        private static bool ValidAmount(float value)
        {
            return value >= 0f && value <= float.MaxValue;
        }

        private static bool VoiceDistancesValid(float near, float far)
        {
            return ValidAmount(near) && ValidAmount(far) && near <= far;
        }
    }
}
