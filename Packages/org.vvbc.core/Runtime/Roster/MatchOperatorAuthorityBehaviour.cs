using Tsukimi;

// Adapter for the future match operation authority. The roster does not decide who runs the
// match: it asks the component assigned to RosterManagerBehaviour.operatorAuthority, which must
// derive from this class (and, for T# visibility, live in this folder or below it).
// With no component assigned nobody is an operator. Instance Master and Roster Owner are never
// operators by themselves.
//
// The answer must be the same on every client: the Roster Owner checks it again before it
// commits a forced release or a reorder of someone else's row.
public abstract class MatchOperatorAuthorityBehaviour : TsukimiBehaviour
{
    public abstract bool IsMatchOperator(int playerId);
}
