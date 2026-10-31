// Stable values shared with BatBehaviour's synced integer fields.
public static class BatGripConstants
{
    public const int GripIdle = 0;
    public const int GripLowerHeld = 1;
    public const int GripTwoHandHeld = 2;
    public const int GripLocked = 3;
    public const int HandNone = 0;
    public const int HandLeft = 1;
    public const int HandRight = 2;
    public const int ProxyNone = 0;
    public const int ProxyA = 1;
    public const int ProxyB = 2;
    public const int ProxyOther = 3;
}

// Transition values are temporary results; the synced fields remain the source of truth.
public struct BatGripTransition
{
    public int state;
    public int lowerHand;
    public int upperHand;
    public bool promoted;
    public bool changed;
}

public static class BatGripLogic
{

    public static int HandHoldingProxy(int proxy, int leftProxy, int rightProxy)
    {
        if (proxy == BatGripConstants.ProxyNone) return BatGripConstants.HandNone;
        if (leftProxy == proxy) return BatGripConstants.HandLeft;
        if (rightProxy == proxy) return BatGripConstants.HandRight;
        return BatGripConstants.HandNone;
    }

    public static BatGripTransition ResolvePickup(int state, int lowerHand, int upperHand,
        int lowerHeldHand, int upperHeldHand)
    {
        BatGripTransition result = new BatGripTransition();
        result.state = state;
        result.lowerHand = lowerHand;
        result.upperHand = upperHand;
        if (state == BatGripConstants.GripIdle)
        {
            if (lowerHeldHand == BatGripConstants.HandNone) return result;
            result.state = BatGripConstants.GripLowerHeld;
            result.lowerHand = lowerHeldHand;
            result.upperHand = BatGripConstants.HandNone;
            result.changed = true;
            return result;
        }
        if (lowerHeldHand == BatGripConstants.HandNone)
        {
            if (upperHeldHand != BatGripConstants.HandNone)
            {
                result.state = BatGripConstants.GripLowerHeld;
                result.lowerHand = upperHeldHand;
                result.upperHand = BatGripConstants.HandNone;
                result.promoted = true;
            }
            else
            {
                result.state = BatGripConstants.GripIdle;
                result.lowerHand = BatGripConstants.HandNone;
                result.upperHand = BatGripConstants.HandNone;
            }
            result.changed = true;
            return result;
        }
        result.lowerHand = lowerHeldHand;
        result.upperHand = upperHeldHand != lowerHeldHand ? upperHeldHand : BatGripConstants.HandNone;
        if (state != BatGripConstants.GripLocked)
            result.state = result.upperHand == BatGripConstants.HandNone ? BatGripConstants.GripLowerHeld : BatGripConstants.GripTwoHandHeld;
        result.changed = result.state != state || result.lowerHand != lowerHand || result.upperHand != upperHand;
        return result;
    }

    public static BatGripTransition ResolveUse(int state, int lowerHand, int upperHand, int hand)
    {
        BatGripTransition result = new BatGripTransition();
        result.state = state;
        result.lowerHand = lowerHand;
        result.upperHand = upperHand;
        if (hand == BatGripConstants.HandNone ||
            (hand != lowerHand && hand != upperHand)) return result;
        if (state == BatGripConstants.GripLowerHeld || state == BatGripConstants.GripTwoHandHeld)
        {
            result.state = BatGripConstants.GripLocked;
            result.changed = true;
        }
        else if (state == BatGripConstants.GripLocked)
        {
            result.state = upperHand != BatGripConstants.HandNone ? BatGripConstants.GripTwoHandHeld : BatGripConstants.GripLowerHeld;
            result.changed = true;
        }
        return result;
    }
}
