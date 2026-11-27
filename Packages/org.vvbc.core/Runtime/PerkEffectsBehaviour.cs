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
//   Locomotion   speed multiplier = base multiplier x Max Speed factor (Locomotion scales the
//                acceleration with it); acceleration multiplier = base x Acceleration factor;
//                initial speed = base x Initial Speed factor, capped by the Max Speed Debuff;
//                JumpImpulse = base impulse x sqrt(Jump Height factor), set through Locomotion
//                so the jump lockout and landing cooldown keep working.
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
    private float _appliedBattedTop = -1f, _appliedBattedCap = -1f, _appliedGamma = -1f, _appliedWidthOffset = -1f;
    private float _appliedEarlyYaw = -1f, _appliedLateYaw = -1f;
    private float _appliedCatchMultiplier = 1f;
    private bool _speedBaseCaptured;
    private float _baseSpeedMultiplier = 1f;
    private float _baseAccelerationMultiplier = 1f;
    private float _baseInitialSpeed;
    private float _baseMaximumSpeed;
    private float _baseAcceleration;
    private float _baseEffectiveInitialSpeed;
    private float _appliedSpeedFactor = 1f;
    private float _appliedAccelerationFactor = 1f;
    private float _appliedInitialSpeed = -1f;
    private float _baseJumpImpulse = -1f;
    private float _appliedJumpFactor = 1f;
    private float _appliedFenceImpulse;
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
        ApplyRunning(gravity);
        perks.NotifyStatsChanged();
    }

    // ---- Batting ----

    private void ApplyBatting(float homingRate, bool bunt)
    {
        if (batMotion == null)
        {
            perks.ReportStat(PerkCatalog.AbilityHoming, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityBunt, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityBattedBallSpeed, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilitySweetSpot, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityContactRange, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityTimingEarly, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
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
        ApplyBattingPerks();
        _battingApplied = true;
        perks.ReportStat(PerkCatalog.AbilityHoming, 0f, batMotion.EffectiveHomingRate(), PlayerPerkBehaviour.StatApplied);
        perks.ReportStat(PerkCatalog.AbilityBunt, 0f, batMotion.buntEnabled ? 1f : 0f, PlayerPerkBehaviour.StatApplied);
    }

    // Batted Ball Speed (a buff's top speed or the debuff's cap, km/h), Sweet Spot Tolerance
    // (gamma), Contact Range (cm -> m on the half width) and Timing Tolerance (early / late yaw;
    // 0 keeps the Bat's own symmetric yaw). The first apply writes the neutral values too.
    private void ApplyBattingPerks()
    {
        float standard = PerkCatalog.Standard(PerkCatalog.AbilityBattedBallSpeed);
        float batted = perks.GetValueOrStandard(PerkCatalog.AbilityBattedBallSpeed);
        float top = batted > standard ? batted : 0f;
        float cap = batted < standard ? batted : 0f;
        if ((!_battingApplied || top != _appliedBattedTop || cap != _appliedBattedCap) && batMotion.SetBattedBallSpeed(top, cap))
        {
            _appliedBattedTop = top;
            _appliedBattedCap = cap;
        }
        float gamma = perks.GetValueOrStandard(PerkCatalog.AbilitySweetSpot);
        if ((!_battingApplied || gamma != _appliedGamma) && batMotion.SetSpeedQualityGamma(gamma)) _appliedGamma = gamma;
        float offset = perks.GetValueOrStandard(PerkCatalog.AbilityContactRange) / 100f;
        if ((!_battingApplied || offset != _appliedWidthOffset) && batMotion.SetContactWidthOffset(offset)) _appliedWidthOffset = offset;
        float early = perks.HasEffect(PerkCatalog.AbilityTimingEarly) ? perks.GetEffectValue(PerkCatalog.AbilityTimingEarly) : 0f;
        float late = perks.HasEffect(PerkCatalog.AbilityTimingLate) ? perks.GetEffectValue(PerkCatalog.AbilityTimingLate) : 0f;
        if ((!_battingApplied || early != _appliedEarlyYaw || late != _appliedLateYaw) && batMotion.SetTimingTolerance(early, late))
        {
            _appliedEarlyYaw = early;
            _appliedLateYaw = late;
        }
        perks.ReportStat(PerkCatalog.AbilityBattedBallSpeed, standard, batted, PlayerPerkBehaviour.StatApplied);
        perks.ReportStat(PerkCatalog.AbilitySweetSpot, 1f, batMotion.speedQualityGamma, PlayerPerkBehaviour.StatApplied);
        perks.ReportStat(PerkCatalog.AbilityContactRange, 0f, batMotion.contactWidthOffset * 100f, PlayerPerkBehaviour.StatApplied);
        perks.ReportStat(PerkCatalog.AbilityTimingEarly, batMotion.timingYawDegrees, batMotion.NormalSwingEarlyYaw(), PlayerPerkBehaviour.StatApplied);
        perks.ReportStat(PerkCatalog.AbilityTimingLate, batMotion.timingYawDegrees, batMotion.NormalSwingLateYaw(), PlayerPerkBehaviour.StatApplied);
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

    // Max Speed sets Speed Multiplier (Locomotion scales the acceleration with it, keeping the
    // curve's shape); Acceleration sets Acceleration Multiplier and the initial speed; the Max
    // Speed Debuff caps the initial speed. All from the bases taken before the first change.
    private void ApplyRunning(float gravity)
    {
        if (locomotion == null)
        {
            perks.ReportStat(PerkCatalog.AbilityMaxSpeed, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityJumpHeight, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityAcceleration, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            perks.ReportStat(PerkCatalog.AbilityInitialSpeed, 0f, 0f, PlayerPerkBehaviour.StatUnavailable);
            return;
        }
        if (!_speedBaseCaptured)
        {
            _speedBaseCaptured = true;
            _baseSpeedMultiplier = locomotion.speedMultiplier;
            _baseAccelerationMultiplier = locomotion.accelerationMultiplier;
            _baseInitialSpeed = locomotion.initialSpeed;
            _baseMaximumSpeed = locomotion.GetEffectiveMaximumSpeed();
            _baseAcceleration = locomotion.GetEffectiveAcceleration();
            _baseEffectiveInitialSpeed = locomotion.GetEffectiveInitialSpeed();
        }
        float speedFactor = perks.GetFactor(PerkCatalog.AbilityMaxSpeed);
        if (speedFactor != _appliedSpeedFactor)
        {
            locomotion.SetSpeedMultiplier(_baseSpeedMultiplier * speedFactor);
            _appliedSpeedFactor = speedFactor;
        }
        float accelerationFactor = perks.GetFactor(PerkCatalog.AbilityAcceleration);
        if (accelerationFactor != _appliedAccelerationFactor)
        {
            locomotion.SetAccelerationMultiplier(_baseAccelerationMultiplier * accelerationFactor);
            _appliedAccelerationFactor = accelerationFactor;
        }
        float initial = InitialSpeedFor(_baseInitialSpeed, perks.GetFactor(PerkCatalog.AbilityInitialSpeed),
            perks.HasEffect(PerkCatalog.AbilityInitialSpeedCap), perks.GetEffectValue(PerkCatalog.AbilityInitialSpeedCap));
        if (initial != _appliedInitialSpeed)
        {
            if (initial != locomotion.initialSpeed) locomotion.SetInitialSpeed(initial);
            _appliedInitialSpeed = initial;
        }
        // Locomotion keeps its own limits (12 m/s ceiling, initial speed under the maximum); the
        // reported final values are what it uses.
        perks.ReportStat(PerkCatalog.AbilityMaxSpeed, _baseMaximumSpeed, locomotion.GetEffectiveMaximumSpeed(), PlayerPerkBehaviour.StatApplied);
        perks.ReportStat(PerkCatalog.AbilityAcceleration, _baseAcceleration, locomotion.GetEffectiveAcceleration(), PlayerPerkBehaviour.StatApplied);
        perks.ReportStat(PerkCatalog.AbilityInitialSpeed, _baseEffectiveInitialSpeed, locomotion.GetEffectiveInitialSpeed(), PlayerPerkBehaviour.StatApplied);
        ApplyJump(perks.GetFactor(PerkCatalog.AbilityJumpHeight), gravity);
    }

    // Impulse for the fence height from the base impulse (height factor 1.30 / 0.46, square
    // root); 0 without the perk or a base.
    public static float FenceJumpImpulse(float baseImpulse, bool jumpPerk)
    {
        if (!jumpPerk || !(baseImpulse > 0f)) return 0f;
        float standard = PerkCatalog.Standard(PerkCatalog.AbilityJumpHeight);
        return baseImpulse * PerkStats.JumpImpulseFactor(PerkCatalog.JumpFenceHeight / standard);
    }

    // base x factor, then at most the cap on the same scale (cap / standard x base) when a cap
    // effect is selected. A non-positive or non-finite result keeps the base.
    public static float InitialSpeedFor(float baseSpeed, float factor, bool capped, float capValue)
    {
        float speed = PerkStats.ApplyFactor(baseSpeed, factor);
        if (capped)
        {
            float standard = PerkCatalog.Standard(PerkCatalog.AbilityInitialSpeed);
            float cap = standard > 0f && PerkStats.Finite(capValue) ? baseSpeed * capValue / standard : speed;
            if (cap < speed) speed = cap;
        }
        return PerkStats.Finite(speed) && speed >= 0f ? speed : baseSpeed;
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
        // Fence jump zones: with a Jump perk, the fence height's impulse from the same base;
        // without one, none (the normal impulse inside a zone too).
        float fence = FenceJumpImpulse(_baseJumpImpulse, perks.HasEffect(PerkCatalog.AbilityJumpHeight));
        if (fence != _appliedFenceImpulse)
        {
            locomotion.SetFenceJumpImpulse(fence);
            _appliedFenceImpulse = fence;
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
