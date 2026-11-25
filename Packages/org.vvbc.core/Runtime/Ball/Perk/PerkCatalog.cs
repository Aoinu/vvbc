// Player perk catalog (docs/development/player-perks.md): every perk the design defines, under a
// stable integer id that never depends on the listing order or the display name. Selection rules
// are in PerkRules, ability values in PerkStats.
//
// Everything here is a switch on the id instead of a lookup array: a static readonly array of a
// plain static class is copied into every program that can see it.
//
// No cost or slot data is used anywhere yet (balance test stage: no points, no slot limit).
public static class PerkCatalog
{
    // ---- Categories (UI tabs) ----
    public const int CategoryBatting = 0;
    public const int CategoryPitching = 1;
    public const int CategoryRunning = 2;
    public const int CategoryFielding = 3;
    public const int CategoryVisual = 4;
    public const int CategoryCount = 5;

    // ---- Abilities (what an effect changes; buff/debuff exclusion is per ability) ----
    public const int AbilityNone = 0;
    public const int AbilityBunt = 1;
    public const int AbilityHoming = 2;
    public const int AbilityPitchFade = 3;
    public const int AbilityLandingIndicator = 4;
    public const int AbilityMaxSpeed = 5;
    public const int AbilityAcceleration = 6;
    public const int AbilityJumpHeight = 7;
    public const int AbilityCatchRadius = 8;
    public const int AbilityPitchSpeedCap = 9;
    public const int AbilityThrowSpeedCap = 10;
    public const int AbilitySpin = 11;
    public const int AbilityControl = 12;
    // Abilities are 1..AbilityCount.
    public const int AbilityCount = 12;

    // ---- Effect kinds ----
    // On/off: the ability is on while a perk with this effect is selected. Value 1.
    public const int KindFlag = 1;
    // One level of a tiered effect; value is the level's parameter (Homing rate, Fade level,
    // Spin/Control tier). At most one perk per ability can hold it (same series).
    public const int KindLevel = 2;
    // Value is the ability at the design standard (Standard(ability)); the modifier is
    // value / standard - 1, and modifiers of all selected perks add up on the base value.
    public const int KindPercent = 3;
    // Value is an upper limit (km/h); not composed with percentages.
    public const int KindCap = 4;

    // ---- Series (stage groups: different tiers of one series exclude each other) ----
    public const int GroupNone = 0;
    public const int GroupBunt = 1;
    public const int GroupHoming = 2;
    public const int GroupPitchFade = 3;
    public const int GroupLandingIndicator = 4;
    public const int GroupMaxSpeed = 5;
    public const int GroupMaxSpeedDebuff = 6;
    public const int GroupAcceleration = 7;
    public const int GroupAccelerationDebuff = 8;
    public const int GroupJump = 9;
    public const int GroupCatchRadius = 10;
    public const int GroupCatchRadiusDebuff = 11;
    public const int GroupBallSpeed = 12;
    public const int GroupBallSpeedDebuff = 13;
    public const int GroupSpin = 14;
    public const int GroupSpinDebuff = 15;
    public const int GroupControl = 16;
    public const int GroupControlDebuff = 17;

    // ---- Perk ids (stable; never reuse or renumber) ----
    public const int Bunt = 1001;
    public const int HomingI = 1011;
    public const int HomingII = 1012;
    public const int HomingIII = 1013;
    public const int BallSpeedDebuff = 2001;
    public const int BallSpeedI = 2011;
    public const int BallSpeedII = 2012;
    public const int BallSpeedIII = 2013;
    public const int SpinDebuff = 2021;
    public const int SpinI = 2031;
    public const int SpinII = 2032;
    public const int SpinIII = 2033;
    public const int ControlDebuff = 2041;
    public const int ControlI = 2051;
    public const int ControlII = 2052;
    public const int ControlIII = 2053;
    public const int MaxSpeedDebuff = 3001;
    public const int MaxSpeedI = 3011;
    public const int MaxSpeedII = 3012;
    public const int MaxSpeedIII = 3013;
    public const int AccelerationDebuff = 3021;
    public const int AccelerationI = 3031;
    public const int AccelerationII = 3032;
    public const int AccelerationIII = 3033;
    public const int JumpI = 3041;
    public const int CatchRadiusDebuff = 4001;
    public const int CatchRadiusI = 4011;
    public const int CatchRadiusII = 4012;
    public const int CatchRadiusIII = 4013;
    public const int PitchFadeI = 5001;
    public const int PitchFadeII = 5002;
    public const int LandingIndicator = 5011;

    public const int PerkCount = 32;

    // Listing order (UI): by category, then series, debuff first.
    public static int IdAt(int index)
    {
        switch (index)
        {
            case 0: return Bunt;
            case 1: return HomingI;
            case 2: return HomingII;
            case 3: return HomingIII;
            case 4: return BallSpeedDebuff;
            case 5: return BallSpeedI;
            case 6: return BallSpeedII;
            case 7: return BallSpeedIII;
            case 8: return SpinDebuff;
            case 9: return SpinI;
            case 10: return SpinII;
            case 11: return SpinIII;
            case 12: return ControlDebuff;
            case 13: return ControlI;
            case 14: return ControlII;
            case 15: return ControlIII;
            case 16: return MaxSpeedDebuff;
            case 17: return MaxSpeedI;
            case 18: return MaxSpeedII;
            case 19: return MaxSpeedIII;
            case 20: return AccelerationDebuff;
            case 21: return AccelerationI;
            case 22: return AccelerationII;
            case 23: return AccelerationIII;
            case 24: return JumpI;
            case 25: return CatchRadiusDebuff;
            case 26: return CatchRadiusI;
            case 27: return CatchRadiusII;
            case 28: return CatchRadiusIII;
            case 29: return PitchFadeI;
            case 30: return PitchFadeII;
            case 31: return LandingIndicator;
        }
        return 0;
    }

    public static bool IsKnown(int id) { return Group(id) != GroupNone; }

    // Series of the perk; GroupNone for an unknown id.
    public static int Group(int id)
    {
        switch (id)
        {
            case Bunt: return GroupBunt;
            case HomingI: case HomingII: case HomingIII: return GroupHoming;
            case BallSpeedDebuff: return GroupBallSpeedDebuff;
            case BallSpeedI: case BallSpeedII: case BallSpeedIII: return GroupBallSpeed;
            case SpinDebuff: return GroupSpinDebuff;
            case SpinI: case SpinII: case SpinIII: return GroupSpin;
            case ControlDebuff: return GroupControlDebuff;
            case ControlI: case ControlII: case ControlIII: return GroupControl;
            case MaxSpeedDebuff: return GroupMaxSpeedDebuff;
            case MaxSpeedI: case MaxSpeedII: case MaxSpeedIII: return GroupMaxSpeed;
            case AccelerationDebuff: return GroupAccelerationDebuff;
            case AccelerationI: case AccelerationII: case AccelerationIII: return GroupAcceleration;
            case JumpI: return GroupJump;
            case CatchRadiusDebuff: return GroupCatchRadiusDebuff;
            case CatchRadiusI: case CatchRadiusII: case CatchRadiusIII: return GroupCatchRadius;
            case PitchFadeI: case PitchFadeII: return GroupPitchFade;
            case LandingIndicator: return GroupLandingIndicator;
        }
        return GroupNone;
    }

    public static int Category(int id)
    {
        switch (Group(id))
        {
            case GroupBunt: case GroupHoming: return CategoryBatting;
            case GroupBallSpeed: case GroupBallSpeedDebuff: case GroupSpin: case GroupSpinDebuff:
            case GroupControl: case GroupControlDebuff: return CategoryPitching;
            case GroupMaxSpeed: case GroupMaxSpeedDebuff: case GroupAcceleration: case GroupAccelerationDebuff:
            case GroupJump: return CategoryRunning;
            case GroupCatchRadius: case GroupCatchRadiusDebuff: return CategoryFielding;
        }
        return CategoryVisual;
    }

    public static bool IsDebuff(int id)
    {
        switch (id)
        {
            case BallSpeedDebuff: case SpinDebuff: case ControlDebuff: case MaxSpeedDebuff:
            case AccelerationDebuff: case CatchRadiusDebuff: return true;
        }
        return false;
    }

    // +1 buff, -1 debuff (the sign of every effect of the perk).
    public static int Sign(int id) { return IsDebuff(id) ? -1 : 1; }

    // Stage within the series: 1..3 (I..III); 0 for a single-stage perk or a debuff.
    public static int Tier(int id)
    {
        switch (id)
        {
            case HomingI: case BallSpeedI: case SpinI: case ControlI: case MaxSpeedI: case AccelerationI:
            case JumpI: case CatchRadiusI: case PitchFadeI: return 1;
            case HomingII: case BallSpeedII: case SpinII: case ControlII: case MaxSpeedII: case AccelerationII:
            case CatchRadiusII: case PitchFadeII: return 2;
            case HomingIII: case BallSpeedIII: case SpinIII: case ControlIII: case MaxSpeedIII: case AccelerationIII:
            case CatchRadiusIII: return 3;
        }
        return 0;
    }

    // Selectable now: its effect reaches the game through a confirmed mapping. The others are
    // in the catalog with their design values, but cannot be selected (see NotImplementedReason).
    public static bool IsImplemented(int id)
    {
        switch (Group(id))
        {
            case GroupBunt: case GroupHoming: case GroupPitchFade: case GroupLandingIndicator:
            case GroupMaxSpeed: case GroupMaxSpeedDebuff: case GroupJump:
            case GroupCatchRadius: case GroupCatchRadiusDebuff: return true;
        }
        return false;
    }

    public static string NotImplementedReason(int id)
    {
        switch (Group(id))
        {
            case GroupAcceleration: case GroupAccelerationDebuff:
                return "Values were set for a 10 m/s² standard; the current model uses 7.5 m/s² with a 2 m/s initial speed. Mapping pending.";
            case GroupBallSpeed: case GroupBallSpeedDebuff:
                return "Perk caps assume 120 km/h; the current pitch (140 km/h) and throw (90-145 km/h) settings differ. Mapping pending.";
            case GroupSpin: case GroupSpinDebuff:
                return "Perk values assume a 2200 rpm 4-Seam; the current pitch model uses 2500 rpm. Mapping pending.";
            case GroupControl: case GroupControlDebuff:
                return "Mapping from the zone-division targets to the current control error is not defined.";
        }
        return "";
    }

    public static string SeriesName(int id)
    {
        switch (Group(id))
        {
            case GroupBunt: return "Bunt";
            case GroupHoming: return "Homing";
            case GroupBallSpeed: case GroupBallSpeedDebuff: return "Ball Speed";
            case GroupSpin: case GroupSpinDebuff: return "Spin";
            case GroupControl: case GroupControlDebuff: return "Control";
            case GroupMaxSpeed: case GroupMaxSpeedDebuff: return "Max Speed";
            case GroupAcceleration: case GroupAccelerationDebuff: return "Acceleration";
            case GroupJump: return "Jump";
            case GroupCatchRadius: case GroupCatchRadiusDebuff: return "Catch Radius";
            case GroupPitchFade: return "Pitch Fade";
            case GroupLandingIndicator: return "Landing Indicator";
        }
        return "Unknown";
    }

    // Display name: the ability name, plus the stage. No invented proper names.
    public static string Name(int id)
    {
        if (!IsKnown(id)) return "Unknown perk " + id;
        if (IsDebuff(id)) return SeriesName(id) + " Debuff";
        int tier = Tier(id);
        if (tier == 1) return SeriesName(id) + " I";
        if (tier == 2) return SeriesName(id) + " II";
        if (tier == 3) return SeriesName(id) + " III";
        return SeriesName(id);
    }

    public static string StageText(int id)
    {
        if (IsDebuff(id)) return "Debuff";
        int tier = Tier(id);
        if (tier == 1) return "I";
        if (tier == 2) return "II";
        if (tier == 3) return "III";
        return "-";
    }

    public static string Description(int id)
    {
        switch (Group(id))
        {
            case GroupBunt: return "Bunt detection on the normal contact path: 1.5x contact width, Power capped at 30%.";
            case GroupHoming: return "Moves a normal swing's contact point toward the sweet spot (timing is not corrected).";
            case GroupBallSpeed: case GroupBallSpeedDebuff: return "Upper limit of pitch speed and of throw speed (one perk, two limits).";
            case GroupSpin: case GroupSpinDebuff: return "Spin of each pitch type (low-spin types have their own values).";
            case GroupControl: case GroupControlDebuff: return "Pitch control target precision.";
            case GroupMaxSpeed: case GroupMaxSpeedDebuff: return "Maximum running speed.";
            case GroupAcceleration: case GroupAccelerationDebuff: return "Running acceleration.";
            case GroupJump: return "Jump height (fence areas: 1.30 m, not implemented yet).";
            case GroupCatchRadius: case GroupCatchRadiusDebuff: return "Glove catch radius, independent of the glove calibration scale.";
            case GroupPitchFade: return "Local rings on the predicted pitch path, shown to the batter.";
            case GroupLandingIndicator: return "Local circle where a batted ball will first touch the ground.";
        }
        return "";
    }

    // Effect amount as the design states it.
    public static string EffectText(int id)
    {
        switch (id)
        {
            case Bunt: return "On";
            case HomingI: return "25%";
            case HomingII: return "50%";
            case HomingIII: return "75%";
            case BallSpeedDebuff: return "Pitch 105 / Throw 90 km/h (std 120 / 120)";
            case BallSpeedI: return "Pitch 140 / Throw 135 km/h";
            case BallSpeedII: return "Pitch 150 / Throw 145 km/h";
            case BallSpeedIII: return "Pitch 160 / Throw 155 km/h";
            case SpinDebuff: return "4S 2000, x0.75, Split 1300, Chg 1800, Knuckle 0.030";
            case SpinI: return "4S 2300, x1.15, Split 650, Chg 1550, Knuckle 0.045";
            case SpinII: return "4S 2500, x1.27, Split 400, Chg 1450, Knuckle 0.050";
            case SpinIII: return "4S 2800, x1.40, Split 180, Chg 1350, Knuckle 0.055";
            case ControlDebuff: return "Whole zone (std 2 parts)";
            case ControlI: return "4 parts";
            case ControlII: return "9 parts";
            case ControlIII: return "About one ball";
            case MaxSpeedDebuff: return "6.0 m/s (std 8.0)";
            case MaxSpeedI: return "8.4 m/s";
            case MaxSpeedII: return "9.0 m/s";
            case MaxSpeedIII: return "9.6 m/s";
            case AccelerationDebuff: return "6.0 m/s² (std 10.0)";
            case AccelerationI: return "11.0 m/s²";
            case AccelerationII: return "12.0 m/s²";
            case AccelerationIII: return "14.0 m/s²";
            case JumpI: return "0.55 m (std 0.46)";
            case CatchRadiusDebuff: return "8 cm (std 12)";
            case CatchRadiusI: return "14 cm";
            case CatchRadiusII: return "16 cm";
            case CatchRadiusIII: return "18 cm";
            case PitchFadeI: return "2 rings";
            case PitchFadeII: return "4 rings";
            case LandingIndicator: return "On";
        }
        return "";
    }

    // ---- Effects ----

    public static int EffectCount(int id)
    {
        if (!IsKnown(id)) return 0;
        return Group(id) == GroupBallSpeed || Group(id) == GroupBallSpeedDebuff ? 2 : 1;
    }

    public static int EffectAbility(int id, int effect)
    {
        switch (Group(id))
        {
            case GroupBunt: return AbilityBunt;
            case GroupHoming: return AbilityHoming;
            case GroupPitchFade: return AbilityPitchFade;
            case GroupLandingIndicator: return AbilityLandingIndicator;
            case GroupMaxSpeed: case GroupMaxSpeedDebuff: return AbilityMaxSpeed;
            case GroupAcceleration: case GroupAccelerationDebuff: return AbilityAcceleration;
            case GroupJump: return AbilityJumpHeight;
            case GroupCatchRadius: case GroupCatchRadiusDebuff: return AbilityCatchRadius;
            case GroupBallSpeed: case GroupBallSpeedDebuff: return effect == 0 ? AbilityPitchSpeedCap : AbilityThrowSpeedCap;
            case GroupSpin: case GroupSpinDebuff: return AbilitySpin;
            case GroupControl: case GroupControlDebuff: return AbilityControl;
        }
        return AbilityNone;
    }

    public static int AbilityKind(int ability)
    {
        switch (ability)
        {
            case AbilityBunt: case AbilityLandingIndicator: return KindFlag;
            case AbilityHoming: case AbilityPitchFade: case AbilitySpin: case AbilityControl: return KindLevel;
            case AbilityMaxSpeed: case AbilityAcceleration: case AbilityJumpHeight: case AbilityCatchRadius: return KindPercent;
            case AbilityPitchSpeedCap: case AbilityThrowSpeedCap: return KindCap;
        }
        return 0;
    }

    // The design value of the effect (see the kinds above).
    public static float EffectValue(int id, int effect)
    {
        switch (id)
        {
            case Bunt: return 1f;
            case HomingI: return 0.25f;
            case HomingII: return 0.50f;
            case HomingIII: return 0.75f;
            case PitchFadeI: return 1f;
            case PitchFadeII: return 2f;
            case LandingIndicator: return 1f;
            case MaxSpeedDebuff: return 6.0f;
            case MaxSpeedI: return 8.4f;
            case MaxSpeedII: return 9.0f;
            case MaxSpeedIII: return 9.6f;
            case AccelerationDebuff: return 6.0f;
            case AccelerationI: return 11.0f;
            case AccelerationII: return 12.0f;
            case AccelerationIII: return 14.0f;
            case JumpI: return 0.55f;
            case CatchRadiusDebuff: return 8f;
            case CatchRadiusI: return 14f;
            case CatchRadiusII: return 16f;
            case CatchRadiusIII: return 18f;
            case BallSpeedDebuff: return effect == 0 ? 105f : 90f;
            case BallSpeedI: return effect == 0 ? 140f : 135f;
            case BallSpeedII: return effect == 0 ? 150f : 145f;
            case BallSpeedIII: return effect == 0 ? 160f : 155f;
            case SpinDebuff: case ControlDebuff: return -1f;
        }
        return Tier(id);
    }

    // The ability at the design standard (no perk), in the unit of EffectValue.
    public static float Standard(int ability)
    {
        switch (ability)
        {
            case AbilityMaxSpeed: return 8.0f;
            case AbilityAcceleration: return 10.0f;
            case AbilityJumpHeight: return 0.46f;
            case AbilityCatchRadius: return 12f;
            case AbilityPitchSpeedCap: return 120f;
            case AbilityThrowSpeedCap: return 120f;
        }
        return 0f;
    }

    public static string AbilityName(int ability)
    {
        switch (ability)
        {
            case AbilityBunt: return "Bunt";
            case AbilityHoming: return "Homing";
            case AbilityPitchFade: return "Pitch Fade";
            case AbilityLandingIndicator: return "Landing Indicator";
            case AbilityMaxSpeed: return "Max Speed";
            case AbilityAcceleration: return "Acceleration";
            case AbilityJumpHeight: return "Jump Height";
            case AbilityCatchRadius: return "Catch Radius";
            case AbilityPitchSpeedCap: return "Pitch Speed Cap";
            case AbilityThrowSpeedCap: return "Throw Speed Cap";
            case AbilitySpin: return "Spin";
            case AbilityControl: return "Control";
        }
        return "Unknown";
    }

    public static string CategoryName(int category)
    {
        switch (category)
        {
            case CategoryBatting: return "Batting";
            case CategoryPitching: return "Pitching";
            case CategoryRunning: return "Running";
            case CategoryFielding: return "Fielding";
            case CategoryVisual: return "Visual Aids";
        }
        return "";
    }
}
