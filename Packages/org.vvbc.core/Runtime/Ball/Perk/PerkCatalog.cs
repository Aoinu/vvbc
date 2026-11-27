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
    // Run/Strafe speed given at once on a push from rest (Acceleration perks).
    public const int AbilityInitialSpeed = 13;
    // Upper limit of the initial speed (Max Speed Debuff), on the standard's scale.
    public const int AbilityInitialSpeedCap = 14;
    // Batted ball speed limit (km/h): a buff's value is the top speed its bonus reaches, the
    // debuff's a hard cap.
    public const int AbilityBattedBallSpeed = 15;
    // Exponent gamma on the contact quality used for the batted ball speed.
    public const int AbilitySweetSpot = 16;
    // Offset (cm) on the normal swing's contact half width.
    public const int AbilityContactRange = 17;
    // Bat yaw tolerance (degrees) on the early and on the late side.
    public const int AbilityTimingEarly = 18;
    public const int AbilityTimingLate = 19;
    // Abilities are 1..AbilityCount.
    public const int AbilityCount = 19;

    // ---- Effect kinds ----
    // On/off: the ability is on while a perk with this effect is selected. Value 1.
    public const int KindFlag = 1;
    // One level of a tiered effect; value is the level's parameter (Homing rate, Fade level,
    // Spin/Control stage, gamma, width offset, yaw). At most one perk per ability can hold it
    // (same series, or buff against debuff).
    public const int KindLevel = 2;
    // Value is the ability at the design standard (Standard(ability)); the modifier is
    // value / standard - 1, and modifiers of all selected perks add up on the base value.
    public const int KindPercent = 3;
    // Value is an upper limit (km/h, or m/s for the initial speed); not composed with percentages.
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
    public const int GroupBattedBallSpeed = 18;
    public const int GroupBattedBallSpeedDebuff = 19;
    public const int GroupSweetSpot = 20;
    public const int GroupSweetSpotDebuff = 21;
    public const int GroupContactRange = 22;
    public const int GroupContactRangeDebuff = 23;
    public const int GroupTiming = 24;

    // ---- Perk ids (stable; never reuse or renumber) ----
    public const int Bunt = 1001;
    public const int HomingI = 1011;
    public const int HomingII = 1012;
    public const int HomingIII = 1013;
    public const int BattedBallSpeedDebuff = 1021;
    public const int BattedBallSpeedI = 1031;
    public const int BattedBallSpeedII = 1032;
    public const int BattedBallSpeedIII = 1033;
    public const int SweetSpotDebuff = 1041;
    public const int SweetSpotI = 1051;
    public const int SweetSpotII = 1052;
    public const int SweetSpotIII = 1053;
    public const int ContactRangeDebuff = 1061;
    public const int ContactRangeI = 1071;
    public const int TimingI = 1081;
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

    public const int PerkCount = 43;

    // Jump I's height inside a fence jump zone (m; the normal area's is its effect value).
    public const float JumpFenceHeight = 1.30f;

    // Listing order (UI): by category, then series, debuff first.
    public static int IdAt(int index)
    {
        switch (index)
        {
            case 0: return Bunt;
            case 1: return HomingI;
            case 2: return HomingII;
            case 3: return HomingIII;
            case 4: return BattedBallSpeedDebuff;
            case 5: return BattedBallSpeedI;
            case 6: return BattedBallSpeedII;
            case 7: return BattedBallSpeedIII;
            case 8: return SweetSpotDebuff;
            case 9: return SweetSpotI;
            case 10: return SweetSpotII;
            case 11: return SweetSpotIII;
            case 12: return ContactRangeDebuff;
            case 13: return ContactRangeI;
            case 14: return TimingI;
            case 15: return BallSpeedDebuff;
            case 16: return BallSpeedI;
            case 17: return BallSpeedII;
            case 18: return BallSpeedIII;
            case 19: return SpinDebuff;
            case 20: return SpinI;
            case 21: return SpinII;
            case 22: return SpinIII;
            case 23: return ControlDebuff;
            case 24: return ControlI;
            case 25: return ControlII;
            case 26: return ControlIII;
            case 27: return MaxSpeedDebuff;
            case 28: return MaxSpeedI;
            case 29: return MaxSpeedII;
            case 30: return MaxSpeedIII;
            case 31: return AccelerationDebuff;
            case 32: return AccelerationI;
            case 33: return AccelerationII;
            case 34: return AccelerationIII;
            case 35: return JumpI;
            case 36: return CatchRadiusDebuff;
            case 37: return CatchRadiusI;
            case 38: return CatchRadiusII;
            case 39: return CatchRadiusIII;
            case 40: return PitchFadeI;
            case 41: return PitchFadeII;
            case 42: return LandingIndicator;
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
            case BattedBallSpeedDebuff: return GroupBattedBallSpeedDebuff;
            case BattedBallSpeedI: case BattedBallSpeedII: case BattedBallSpeedIII: return GroupBattedBallSpeed;
            case SweetSpotDebuff: return GroupSweetSpotDebuff;
            case SweetSpotI: case SweetSpotII: case SweetSpotIII: return GroupSweetSpot;
            case ContactRangeDebuff: return GroupContactRangeDebuff;
            case ContactRangeI: return GroupContactRange;
            case TimingI: return GroupTiming;
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
            case GroupBunt: case GroupHoming: case GroupBattedBallSpeed: case GroupBattedBallSpeedDebuff:
            case GroupSweetSpot: case GroupSweetSpotDebuff: case GroupContactRange: case GroupContactRangeDebuff:
            case GroupTiming: return CategoryBatting;
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
            case AccelerationDebuff: case CatchRadiusDebuff: case BattedBallSpeedDebuff:
            case SweetSpotDebuff: case ContactRangeDebuff: return true;
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
            case JumpI: case CatchRadiusI: case PitchFadeI: case BattedBallSpeedI: case SweetSpotI:
            case ContactRangeI: case TimingI: return 1;
            case HomingII: case BallSpeedII: case SpinII: case ControlII: case MaxSpeedII: case AccelerationII:
            case CatchRadiusII: case PitchFadeII: case BattedBallSpeedII: case SweetSpotII: return 2;
            case HomingIII: case BallSpeedIII: case SpinIII: case ControlIII: case MaxSpeedIII: case AccelerationIII:
            case CatchRadiusIII: case BattedBallSpeedIII: case SweetSpotIII: return 3;
        }
        return 0;
    }

    // Selectable now: its effect reaches the game through a connected, tested system. Every
    // designed perk is connected; NotImplementedReason stays for a perk added before it is.
    public static bool IsImplemented(int id)
    {
        return IsKnown(id);
    }

    public static string NotImplementedReason(int id)
    {
        return "";
    }

    public static string SeriesName(int id)
    {
        switch (Group(id))
        {
            case GroupBunt: return "Bunt";
            case GroupHoming: return "Homing";
            case GroupBattedBallSpeed: case GroupBattedBallSpeedDebuff: return "Batted Ball Speed";
            case GroupSweetSpot: case GroupSweetSpotDebuff: return "Sweet Spot Tolerance";
            case GroupContactRange: case GroupContactRangeDebuff: return "Contact Range";
            case GroupTiming: return "Timing Tolerance";
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
            case GroupBattedBallSpeed: return "Extra exit speed for high-Power, high-quality normal swings only (Power 0.85-1, quality 0.90-1).";
            case GroupBattedBallSpeedDebuff: return "Hard cap on the exit speed; slower hits are unchanged.";
            case GroupSweetSpot: case GroupSweetSpotDebuff: return "Exit speed uses quality^gamma (normal swing); contact, angle and spin unchanged.";
            case GroupContactRange: case GroupContactRangeDebuff: return "Normal swing contact half width offset; the added outer band gives weak contact.";
            case GroupTiming: return "Bat yaw tolerance of a normal swing: early and late side separately.";
            case GroupBallSpeed: case GroupBallSpeedDebuff: return "Upper limit of the 4-Seam pitch speed and of the throw speed (one perk, two limits).";
            case GroupSpin: case GroupSpinDebuff: return "Hand pitch spin: 4-Seam rpm, breaking ball multiplier, Split / Change rpm, Knuckle SSW.";
            case GroupControl: case GroupControlDebuff: return "Hand pitch control error for the same control score (sigma at score 0).";
            case GroupMaxSpeed: case GroupMaxSpeedDebuff: return "Maximum running speed; the acceleration scales with it, so the speed curve keeps its shape.";
            case GroupAcceleration: case GroupAccelerationDebuff: return "First step: the speed given at once from rest, and the acceleration.";
            case GroupJump: return "Jump height; 1.30 m inside fence jump zones.";
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
            case BattedBallSpeedDebuff: return "Cap 144 km/h (std 162)";
            case BattedBallSpeedI: return "Up to 172 km/h";
            case BattedBallSpeedII: return "Up to 185 km/h";
            case BattedBallSpeedIII: return "Up to 200 km/h";
            case SweetSpotDebuff: return "gamma 1.25 (std 1.00)";
            case SweetSpotI: return "gamma 0.90";
            case SweetSpotII: return "gamma 0.80";
            case SweetSpotIII: return "gamma 0.70";
            case ContactRangeDebuff: return "-2.0 cm half width (std 0)";
            case ContactRangeI: return "+2.5 cm half width";
            case TimingI: return "Early 60 / Late 45 deg (std 30 / 30)";
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
            case MaxSpeedDebuff: return "7.5 m/s, initial max 2.0 (std 8.0)";
            case MaxSpeedI: return "8.9 m/s";
            case MaxSpeedII: return "9.4 m/s";
            case MaxSpeedIII: return "9.9 m/s";
            case AccelerationDebuff: return "Accel x0.8, initial 2.0 m/s (std x1.0, 3.0)";
            case AccelerationI: return "Accel x1.1, initial 3.2 m/s";
            case AccelerationII: return "Accel x1.2, initial 3.4 m/s";
            case AccelerationIII: return "Accel x1.4, initial 3.6 m/s";
            case JumpI: return "0.55 m, fence 1.30 m (std 0.46)";
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
        switch (Group(id))
        {
            case GroupNone: return 0;
            case GroupBallSpeed: case GroupBallSpeedDebuff:
            case GroupAcceleration: case GroupAccelerationDebuff:
            case GroupMaxSpeedDebuff: case GroupTiming: return 2;
        }
        return 1;
    }

    public static int EffectAbility(int id, int effect)
    {
        switch (Group(id))
        {
            case GroupBunt: return AbilityBunt;
            case GroupHoming: return AbilityHoming;
            case GroupPitchFade: return AbilityPitchFade;
            case GroupLandingIndicator: return AbilityLandingIndicator;
            case GroupMaxSpeed: return AbilityMaxSpeed;
            case GroupMaxSpeedDebuff: return effect == 0 ? AbilityMaxSpeed : AbilityInitialSpeedCap;
            case GroupAcceleration: case GroupAccelerationDebuff: return effect == 0 ? AbilityAcceleration : AbilityInitialSpeed;
            case GroupJump: return AbilityJumpHeight;
            case GroupCatchRadius: case GroupCatchRadiusDebuff: return AbilityCatchRadius;
            case GroupBallSpeed: case GroupBallSpeedDebuff: return effect == 0 ? AbilityPitchSpeedCap : AbilityThrowSpeedCap;
            case GroupSpin: case GroupSpinDebuff: return AbilitySpin;
            case GroupControl: case GroupControlDebuff: return AbilityControl;
            case GroupBattedBallSpeed: case GroupBattedBallSpeedDebuff: return AbilityBattedBallSpeed;
            case GroupSweetSpot: case GroupSweetSpotDebuff: return AbilitySweetSpot;
            case GroupContactRange: case GroupContactRangeDebuff: return AbilityContactRange;
            case GroupTiming: return effect == 0 ? AbilityTimingEarly : AbilityTimingLate;
        }
        return AbilityNone;
    }

    public static int AbilityKind(int ability)
    {
        switch (ability)
        {
            case AbilityBunt: case AbilityLandingIndicator: return KindFlag;
            case AbilityHoming: case AbilityPitchFade: case AbilitySpin: case AbilityControl:
            case AbilitySweetSpot: case AbilityContactRange: case AbilityTimingEarly: case AbilityTimingLate: return KindLevel;
            case AbilityMaxSpeed: case AbilityAcceleration: case AbilityJumpHeight: case AbilityCatchRadius:
            case AbilityInitialSpeed: return KindPercent;
            case AbilityPitchSpeedCap: case AbilityThrowSpeedCap: case AbilityInitialSpeedCap:
            case AbilityBattedBallSpeed: return KindCap;
        }
        return 0;
    }

    // The design value of the effect (see the kinds above). Spin and Control: the stage, -1 for
    // the debuff (their numbers are in the profiles below).
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
            case MaxSpeedDebuff: return effect == 0 ? 7.5f : 2.0f;
            case MaxSpeedI: return 8.9f;
            case MaxSpeedII: return 9.4f;
            case MaxSpeedIII: return 9.9f;
            case AccelerationDebuff: return effect == 0 ? 8.0f : 2.0f;
            case AccelerationI: return effect == 0 ? 11.0f : 3.2f;
            case AccelerationII: return effect == 0 ? 12.0f : 3.4f;
            case AccelerationIII: return effect == 0 ? 14.0f : 3.6f;
            case JumpI: return 0.55f;
            case CatchRadiusDebuff: return 8f;
            case CatchRadiusI: return 14f;
            case CatchRadiusII: return 16f;
            case CatchRadiusIII: return 18f;
            case BallSpeedDebuff: return effect == 0 ? 105f : 90f;
            case BallSpeedI: return effect == 0 ? 140f : 135f;
            case BallSpeedII: return effect == 0 ? 150f : 145f;
            case BallSpeedIII: return effect == 0 ? 160f : 155f;
            case BattedBallSpeedDebuff: return 144f;
            case BattedBallSpeedI: return 172f;
            case BattedBallSpeedII: return 185f;
            case BattedBallSpeedIII: return 200f;
            case SweetSpotDebuff: return 1.25f;
            case SweetSpotI: return 0.90f;
            case SweetSpotII: return 0.80f;
            case SweetSpotIII: return 0.70f;
            case ContactRangeDebuff: return -2.0f;
            case ContactRangeI: return 2.5f;
            case TimingI: return effect == 0 ? 60f : 45f;
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
            case AbilityInitialSpeed: return 3.0f;
            case AbilityInitialSpeedCap: return 3.0f;
            case AbilityJumpHeight: return 0.46f;
            case AbilityCatchRadius: return 12f;
            case AbilityPitchSpeedCap: return 120f;
            case AbilityThrowSpeedCap: return 120f;
            case AbilityBattedBallSpeed: return 162f;
            case AbilitySweetSpot: return 1f;
            case AbilityContactRange: return 0f;
            case AbilityTimingEarly: return 30f;
            case AbilityTimingLate: return 30f;
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
            case AbilityInitialSpeed: return "Initial Speed";
            case AbilityInitialSpeedCap: return "Initial Speed Cap";
            case AbilityBattedBallSpeed: return "Batted Ball Speed";
            case AbilitySweetSpot: return "Sweet Spot Tolerance";
            case AbilityContactRange: return "Contact Range";
            case AbilityTimingEarly: return "Timing Early";
            case AbilityTimingLate: return "Timing Late";
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

    // ---- Spin profile (hand pitches; stage -1 debuff, 0 standard, 1-3 I-III) ----

    public static float SpinFourSeamRpm(int stage)
    {
        switch (stage)
        {
            case -1: return 2000f;
            case 1: return 2300f;
            case 2: return 2500f;
            case 3: return 2800f;
        }
        return 2200f;
    }

    // Multiplier on a general breaking ball's spin at the standard (2-Seam, Sinker, Cutter,
    // Slider, Sweeper, Curve).
    public static float SpinBreakingMultiplier(int stage)
    {
        switch (stage)
        {
            case -1: return 0.75f;
            case 1: return 1.15f;
            case 2: return 1.27f;
            case 3: return 1.40f;
        }
        return 1f;
    }

    // Split spin: lower is better (the rpm goes down with the stage).
    public static float SpinSplitRpm(int stage)
    {
        switch (stage)
        {
            case -1: return 1300f;
            case 1: return 650f;
            case 2: return 400f;
            case 3: return 180f;
        }
        return 900f;
    }

    public static float SpinChangeRpm(int stage)
    {
        switch (stage)
        {
            case -1: return 1800f;
            case 1: return 1550f;
            case 2: return 1450f;
            case 3: return 1350f;
        }
        return 1650f;
    }

    public static float SpinKnuckleSsw(int stage)
    {
        switch (stage)
        {
            case -1: return 0.030f;
            case 1: return 0.045f;
            case 2: return 0.050f;
            case 3: return 0.055f;
        }
        return 0.040f;
    }

    // ---- Control (hand pitches) ----

    // Design default of the control error's 1 sigma at control score 0, in metres (sigma at
    // score 85 = 0.15 x this). Provisional; PlayerPerkBehaviour holds the tunable copies.
    public static float ControlSigmaAtZero(int stage)
    {
        switch (stage)
        {
            case -1: return 1.30f;
            case 1: return 0.60f;
            case 2: return 0.40f;
            case 3: return 0.18f;
        }
        return 0.90f;
    }

    public static string ControlTargetText(int stage)
    {
        switch (stage)
        {
            case -1: return "whole zone";
            case 1: return "4 parts";
            case 2: return "9 parts";
            case 3: return "about one ball";
        }
        return "2 parts";
    }
}
