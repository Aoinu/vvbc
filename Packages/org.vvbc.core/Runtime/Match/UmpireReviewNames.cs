// Names of the umpire engine's review items (UmpireEngineBehaviour.Review*), for panels. A plain
// static class, so other Behaviours can call it.
public static class UmpireReviewNames
{
    public static string Name(int code)
    {
        switch (code)
        {
            case UmpireEngineBehaviour.ReviewNoPitchResult: return "No pitch result";
            case UmpireEngineBehaviour.ReviewContactUnconfirmed: return "Contact not confirmed";
            case UmpireEngineBehaviour.ReviewNoCrossing: return "No plate crossing";
            case UmpireEngineBehaviour.ReviewPitchNotAccepted: return "Pitch not accepted";
            case UmpireEngineBehaviour.ReviewThirdStrikeUnknown: return "Third strike: catch unknown";
            case UmpireEngineBehaviour.ReviewCatchGroundOrder: return "Catch / ground order";
            case UmpireEngineBehaviour.ReviewFairFoulUnknown: return "Fair / foul unknown";
            case UmpireEngineBehaviour.ReviewOutOfPlay: return "Out of play";
            case UmpireEngineBehaviour.ReviewHoldUnverifiable: return "Hold unverifiable";
            case UmpireEngineBehaviour.ReviewForceContested: return "Force play contested";
            case UmpireEngineBehaviour.ReviewTagContested: return "Tag play contested";
            case UmpireEngineBehaviour.ReviewMissedBase: return "Missed base";
            case UmpireEngineBehaviour.ReviewPassing: return "Runner passing";
            case UmpireEngineBehaviour.ReviewAfterCaughtFly: return "Runner after caught fly";
            case UmpireEngineBehaviour.ReviewOverrun: return "First base overrun";
            case UmpireEngineBehaviour.ReviewRunnerConflict: return "Runner on another's base";
            case UmpireEngineBehaviour.ReviewPendingTimeout: return "Decision timed out";
            case UmpireEngineBehaviour.ReviewManualPitchRuling: return "Pitch ruled by hand";
            case UmpireEngineBehaviour.ReviewRulingRefused: return "Ruling refused";
            case UmpireEngineBehaviour.ReviewPitchOverlap: return "Pitch not resolved";
            case UmpireEngineBehaviour.ReviewHitMismatch: return "Hit by another batter";
            case UmpireEngineBehaviour.ReviewGroundUnknown: return "Ground contact unknown";
            case UmpireEngineBehaviour.ReviewTouchUnknown: return "Base contact unknown";
            case UmpireEngineBehaviour.ReviewSwingUnknown: return "Swing unknown";
        }
        return "-";
    }
}
