using UnityEngine;

// Ability values from a perk selection (docs/development/player-perks.md). Always computed from
// the selection and a base value, never from the previous result, so the order of selection and
// of removals never matters:
//   percent abilities  final = base * (1 + sum of (value / standard - 1))
//   level abilities    the value of the one selected stage (0 without), series exclusion
//                      guarantees at most one
//   flag abilities     on while a perk with the effect is selected
//   cap abilities      the limit of the one selected stage (0 without)
// The result is finite: a non-finite modifier counts as none and the factor is kept in
// [MinFactor, MaxFactor]. Each game system still applies its own limits.
public static class PerkStats
{
    public const float MinFactor = 0f;
    public const float MaxFactor = 10f;

    public static float PercentModifier(int[] selected, int count, int ability)
    {
        float standard = PerkCatalog.Standard(ability);
        if (!(standard > 0f) || PerkCatalog.AbilityKind(ability) != PerkCatalog.KindPercent) return 0f;
        float sum = 0f;
        for (int i = 0; i < count; i++)
        {
            int id = selected[i];
            int effects = PerkCatalog.EffectCount(id);
            for (int e = 0; e < effects; e++)
            {
                if (PerkCatalog.EffectAbility(id, e) != ability) continue;
                float modifier = PerkCatalog.EffectValue(id, e) / standard - 1f;
                if (Finite(modifier)) sum += modifier;
            }
        }
        return Finite(sum) ? sum : 0f;
    }

    // 1 + modifier, finite and within [MinFactor, MaxFactor].
    public static float Factor(float modifier)
    {
        if (!Finite(modifier)) return 1f;
        return Mathf.Clamp(1f + modifier, MinFactor, MaxFactor);
    }

    public static float PercentFactor(int[] selected, int count, int ability)
    {
        return Factor(PercentModifier(selected, count, ability));
    }

    // base * factor; a non-finite base gives 0.
    public static float ApplyFactor(float baseValue, float factor)
    {
        if (!Finite(baseValue)) return 0f;
        float result = baseValue * Factor(factor - 1f);
        return Finite(result) ? result : baseValue;
    }

    public static bool HasEffect(int[] selected, int count, int ability)
    {
        for (int i = 0; i < count; i++)
        {
            int id = selected[i];
            int effects = PerkCatalog.EffectCount(id);
            for (int e = 0; e < effects; e++)
                if (PerkCatalog.EffectAbility(id, e) == ability) return true;
        }
        return false;
    }

    // Value of the selected level or cap effect of the ability; 0 without one.
    public static float EffectValue(int[] selected, int count, int ability)
    {
        for (int i = 0; i < count; i++)
        {
            int id = selected[i];
            int effects = PerkCatalog.EffectCount(id);
            for (int e = 0; e < effects; e++)
                if (PerkCatalog.EffectAbility(id, e) == ability) return PerkCatalog.EffectValue(id, e);
        }
        return 0f;
    }

    public static bool Flag(int[] selected, int count, int ability)
    {
        return PerkCatalog.AbilityKind(ability) == PerkCatalog.KindFlag && HasEffect(selected, count, ability);
    }

    // Homing rate in [0, 1]; 0 without a Homing perk.
    public static float HomingRate(int[] selected, int count)
    {
        float rate = EffectValue(selected, count, PerkCatalog.AbilityHoming);
        return Finite(rate) ? Mathf.Clamp01(rate) : 0f;
    }

    // Pitch Fade level 0 (off), 1 (Fade I) or 2 (Fade II).
    public static int FadeLevel(int[] selected, int count)
    {
        float level = EffectValue(selected, count, PerkCatalog.AbilityPitchFade);
        if (!Finite(level)) return 0;
        return Mathf.Clamp(Mathf.RoundToInt(level), 0, 2);
    }

    // Jump height scales with the square of the take-off speed, so a height factor f gives a
    // JumpImpulse factor of sqrt(f).
    public static float JumpImpulseFactor(float heightFactor)
    {
        return Mathf.Sqrt(Factor(heightFactor - 1f));
    }

    // Height reached with a take-off speed under a gravity, in metres (0 for invalid input).
    public static float JumpHeight(float impulse, float gravity)
    {
        if (!Finite(impulse) || !(gravity > 0f) || !Finite(gravity)) return 0f;
        return impulse * impulse / (2f * gravity);
    }

    // NaN fails every comparison; infinities fail the bound.
    public static bool Finite(float value)
    {
        return value > -1e30f && value < 1e30f;
    }
}
