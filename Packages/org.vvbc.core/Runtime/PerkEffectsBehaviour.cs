using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VVBC.Locomotion;

// Applies the local player's perks to the game (docs/development/player-perks.md), on the
// `PerkEffects` child of VVBCPlayerObjectTemplate. It acts only on the client that owns the
// PlayerObject, so a remote player's perks are never applied a second time: their effects reach
// others through the existing synchronized results (batted ball, catch, ...).
//
// Runs only on _OnPerksChanged (and once at Start, which switches the perk-managed assists off
// for the empty selection); nothing runs per frame. Every value is computed from the selection
// and a base taken once before the first change, never from the previous value:
//   Bat Motion   Homing rate (0 / 25 / 50 / 75 %) and Bunt on/off; nothing else of the batting
//                settings is touched.
//   Glove        catch radius multiplier (on the authored trigger radius, before glove scale).
//   Locomotion   speed multiplier = base multiplier x Max Speed factor; JumpImpulse = base
//                impulse x sqrt(Jump Height factor), set through Locomotion so the jump lockout
//                and landing cooldown keep working.
// Pitch Fade and the Landing Indicator sit on every HomePlate and follow the local player's
// perks themselves. What was applied is reported back to PlayerPerkBehaviour for display.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class PerkEffectsBehaviour : TsukimiBehaviour
{
    [Header("References (inside the PlayerObject template)")]
    public PlayerPerkBehaviour perks;
    public BatMotionBehaviour batMotion;
    public GloveBehaviour glove;
    [Header("Scene")]
    [Tooltip("The scene's Locomotion. Set on the template instance in the scene; empty = running perks are not applied.")]
    public LocomotionBehaviour locomotion;

    private bool _battingApplied;
    private float _appliedHomingRate;
    private bool _appliedBunt;
    private float _appliedCatchMultiplier = 1f;
    private bool _speedBaseCaptured;
    private float _baseSpeedMultiplier = 1f;
    private float _baseMaximumSpeed;
    private float _appliedSpeedFactor = 1f;
    private float _baseJumpImpulse = -1f;
    private float _appliedJumpFactor = 1f;
    private int _locomotionWaits;

    void Start()
    {
        if (perks != null) perks.AddChangeListener(GetComponent<UdonBehaviour>());
        Apply();
        if (AppliesHere() && locomotion != null && !(locomotion.GetJumpImpulse() > 0f))
            SendCustomEventDelayedSeconds(nameof(_WaitForLocomotion), 1f);
    }

    // Locomotion activates the local player a frame after joining; until then it has no jump
    // impulse to report or scale. Applies again once it has (a few tries, then gives up).
    public void _WaitForLocomotion()
    {
        if (locomotion == null) return;
        if (locomotion.GetJumpImpulse() > 0f) { Apply(); return; }
        if (_locomotionWaits++ < 10) SendCustomEventDelayedSeconds(nameof(_WaitForLocomotion), 1f);
    }

    public void _OnPerksChanged()
    {
        Apply();
    }

    public bool AppliesHere()
    {
        return perks != null && perks.CanEdit();
    }

    public void Apply()
    {
        if (!AppliesHere()) return;
        ApplyAt(JumpGravity());
    }

    // gravity (m/s²) is used only for the reported jump height.
    public void ApplyAt(float gravity)
    {
        if (!AppliesHere()) return;
        ApplyOwned(gravity);
    }

    // The application itself, for the owner's client (AppliesHere checked by the callers).
    private void ApplyOwned(float gravity)
    {
        if (perks == null) return;
        ApplyBatting(perks.GetHomingRate(), perks.IsFlagOn(PerkCatalog.AbilityBunt));
        ApplyCatch(perks.GetFactor(PerkCatalog.AbilityCatchRadius));
        ApplyRunning(perks.GetFactor(PerkCatalog.AbilityMaxSpeed), perks.GetFactor(PerkCatalog.AbilityJumpHeight), gravity);
        perks.NotifyStatsChanged();
    }

    // ---- Batting ----

    private void ApplyBatting(float homingRate, bool bunt)
    {
        if (batMotion == null)
        {
            perks.ReportStat(PerkCatalog.AbilityHoming, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityBunt, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            return;
        }
        // The first apply always writes, so the assists' Inspector defaults never stay on.
        if (!_battingApplied || homingRate != _appliedHomingRate)
        {
            if (batMotion.SetHoming(homingRate > 0f, homingRate)) _appliedHomingRate = homingRate;
        }
        if (!_battingApplied || bunt != _appliedBunt)
        {
            batMotion.SetBuntEnabled(bunt);
            _appliedBunt = bunt;
        }
        _battingApplied = true;
        perks.ReportStat(PerkCatalog.AbilityHoming, 0f, batMotion.EffectiveHomingRate(), PlayerPerkBehaviour.StatApplied);
        perks.ReportStat(PerkCatalog.AbilityBunt, 0f, batMotion.buntEnabled ? 1f : 0f, PlayerPerkBehaviour.StatApplied);
    }

    // ---- Fielding ----

    private void ApplyCatch(float factor)
    {
        if (glove == null)
        {
            perks.ReportStat(PerkCatalog.AbilityCatchRadius, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            return;
        }
        if (factor != _appliedCatchMultiplier && glove.SetCatchRadiusMultiplier(factor)) _appliedCatchMultiplier = factor;
        perks.ReportStat(PerkCatalog.AbilityCatchRadius, glove.CatchRadiusAt(1f),
            glove.CatchRadiusAt(glove.CatchRadiusMultiplier()), PlayerPerkBehaviour.StatApplied);
    }

    // ---- Running ----

    private void ApplyRunning(float speedFactor, float jumpFactor, float gravity)
    {
        if (locomotion == null)
        {
            perks.ReportStat(PerkCatalog.AbilityMaxSpeed, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityJumpHeight, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityAcceleration, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            return;
        }
        if (!_speedBaseCaptured)
        {
            _speedBaseCaptured = true;
            _baseSpeedMultiplier = locomotion.speedMultiplier;
            _baseMaximumSpeed = locomotion.GetEffectiveMaximumSpeed();
        }
        if (speedFactor != _appliedSpeedFactor)
        {
            locomotion.SetSpeedMultiplier(_baseSpeedMultiplier * speedFactor);
            _appliedSpeedFactor = speedFactor;
        }
        // Locomotion keeps its own 12 m/s ceiling; the reported final value is what it uses.
        perks.ReportStat(PerkCatalog.AbilityMaxSpeed, _baseMaximumSpeed, locomotion.GetEffectiveMaximumSpeed(), PlayerPerkBehaviour.StatApplied);
        // Acceleration perks are not connected yet: the current value is shown, never changed.
        float acceleration = locomotion.acceleration * locomotion.accelerationMultiplier;
        perks.ReportStat(PerkCatalog.AbilityAcceleration, acceleration, acceleration, PlayerPerkBehaviour.StatUnavailable);
        ApplyJump(jumpFactor, gravity);
    }

    private void ApplyJump(float factor, float gravity)
    {
        if (factor != _appliedJumpFactor)
        {
            // The base is the impulse Locomotion uses before the first jump perk; until the local
            // player is activated there is none, and the jump stays as it is.
            if (_baseJumpImpulse < 0f)
            {
                float current = locomotion.GetJumpImpulse();
                if (current > 0f) _baseJumpImpulse = current;
            }
            if (_baseJumpImpulse > 0f)
            {
                locomotion.SetJumpImpulse(_baseJumpImpulse * PerkStats.JumpImpulseFactor(factor));
                _appliedJumpFactor = factor;
            }
        }
        float baseImpulse = _baseJumpImpulse > 0f ? _baseJumpImpulse : locomotion.GetJumpImpulse();
        int state = factor == _appliedJumpFactor && baseImpulse > 0f ? PlayerPerkBehaviour.StatApplied : PlayerPerkBehaviour.StatUnavailable;
        perks.ReportStat(PerkCatalog.AbilityJumpHeight, PerkStats.JumpHeight(baseImpulse, gravity),
            PerkStats.JumpHeight(locomotion.GetJumpImpulse(), gravity), state);
    }

    // The local player's gravity, for the reported jump height only.
    private float JumpGravity()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        float strength = Utilities.IsValid(player) ? player.GetGravityStrength() : 1f;
        return Physics.gravity.magnitude * strength;
    }
}
