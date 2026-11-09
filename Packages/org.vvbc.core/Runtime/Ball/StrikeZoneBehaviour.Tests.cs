using Tsukimi;
using UnityEngine;

public partial class StrikeZoneBehaviour
{
    [TsukimiTest]
    public void OccupantHeightUsesEyeOffsetAndRejectsInvalidValues()
    {
        useBatterOccupancy = true;
        occupantPlayerId = 1;
        Assert.IsTrue(SetEyeHeight(1.7f));
        Assert.IsTrue(Mathf.Abs(EffectiveBottom() - 0.486f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(EffectiveTop() - 0.963f) < 0.00001f);
        Assert.IsFalse(SetEyeHeight(float.NaN));
        Assert.IsFalse(SetEyeHeight(-1f));
        Assert.IsTrue(Mathf.Abs(EffectiveTop() - 0.963f) < 0.00001f);
        topHeightRatio = 0.1f;
        Assert.IsFalse(SetEyeHeight(1.7f));
    }

    [TsukimiTest]
    public void ZoneUsesDefaultWhenUnoccupiedOrSyncIsInvalid()
    {
        useBatterOccupancy = true;
        syncedZoneBottom = 0.5f; syncedZoneTop = 1.1f;
        occupantPlayerId = -1;
        Assert.IsTrue(Mathf.Abs(EffectiveBottom() - 0.486f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(EffectiveTop() - 0.963f) < 0.00001f);
        occupantPlayerId = 2;
        Assert.AreEqual(0.5f, EffectiveBottom());
        Assert.AreEqual(1.1f, EffectiveTop());
        syncedZoneTop = float.NaN;
        Assert.IsTrue(Mathf.Abs(EffectiveTop() - 0.963f) < 0.00001f);
    }

    [TsukimiTest]
    public void OccupiedPlateRejectsAnotherOwnershipRequest()
    {
        var first = Mimic.Join();
        var second = Mimic.Join();
        occupantPlayerId = -1;
        Assert.IsTrue(OnOwnershipRequest(second, second));
        occupantPlayerId = first.playerId;
        Assert.IsFalse(OnOwnershipRequest(second, second));
        Assert.IsTrue(OnOwnershipRequest(first, first));
    }

    [TsukimiTest]
    public void BoxFlagsRequireBothExitsBeforeReleasing()
    {
        var player = Mimic.Join();
        useBatterOccupancy = true;
        occupantPlayerId = player.playerId;
        _insideLeft = true;
        _insideRight = true;
        SetBatterBoxInside(true, false);
        Assert.AreEqual(player.playerId, occupantPlayerId);
        Assert.IsTrue(IsLocalOccupant());
        SetBatterBoxInside(false, false);
        Assert.AreEqual(-1, occupantPlayerId);
        Assert.IsFalse(IsLocalOccupant());
    }

    [TsukimiTest]
    public void WaitingPlayerDoesNotAutomaticallyClaimAfterVacancy()
    {
        var owner = Mimic.Join();
        var waiting = Mimic.Join();
        useBatterOccupancy = true;
        occupantPlayerId = owner.playerId;
        syncedZoneBottom = 0.486f; syncedZoneTop = 0.963f;
        _insideLeft = true;
        RequestSerialization();
        Mimic.Deliver();
        Mimic.Become(waiting);
        SetBatterBoxInside(false, true);
        Assert.AreEqual(owner.playerId, occupantPlayerId);
        Assert.IsFalse(IsLocalOccupant());
        Mimic.Become(owner);
        SetBatterBoxInside(true, false);
        Mimic.Deliver();
        Mimic.Become(waiting);
        Assert.AreEqual(-1, occupantPlayerId);
        Assert.IsFalse(IsLocalOccupant());
        Assert.IsFalse(_claimPending);
    }

    [TsukimiTest]
    public void LateJoinerReceivesOccupantAndHeight()
    {
        var owner = Mimic.Join();
        occupantPlayerId = owner.playerId;
        syncedZoneBottom = 0.54f;
        syncedZoneTop = 1.07f;
        RequestSerialization();
        var late = Mimic.Join();
        Mimic.Become(late);
        useBatterOccupancy = true;
        Assert.AreEqual(owner.playerId, occupantPlayerId);
        Assert.AreEqual(0.54f, EffectiveBottom());
        Assert.AreEqual(1.07f, EffectiveTop());
        Assert.IsFalse(IsLocalOccupant());
    }

    [TsukimiTest]
    public void OwnershipLossImmediatelyDisablesLocalOccupancy()
    {
        var owner = Mimic.Join();
        var other = Mimic.Join();
        useBatterOccupancy = true;
        occupantPlayerId = owner.playerId;
        _insideLeft = true;
        _confirmedLocal = true;
        Assert.IsTrue(IsLocalOccupant());
        OnOwnershipTransferred(other);
        Assert.IsFalse(IsLocalOccupant());
        Assert.IsFalse(_confirmedLocal);
        Assert.IsTrue(_claimPending);
    }

    [TsukimiTest]
    public void PendingClaimSurvivesIntermediateOwnerAndEndsOnExit()
    {
        var first = Mimic.Join();
        var other = Mimic.Join();
        _insideLeft = true;
        _claimPending = true;
        OnOwnershipTransferred(other);
        Assert.IsTrue(_claimPending);
        Assert.IsFalse(IsLocalOccupant());
        occupantPlayerId = other.playerId;
        OnDeserialization();
        Assert.IsTrue(_claimPending);
        Assert.AreNotEqual(first.playerId, occupantPlayerId);
        SetBatterBoxInside(true, false);
        Assert.IsFalse(_claimPending);
    }

    [TsukimiTest]
    public void LostOwnershipCallbackPreventsClaimWhileOwnerQueryIsStale()
    {
        Mimic.Join();
        _insideLeft = true;
        _claimPending = true;
        _ownershipLost = true;
        CompleteClaim();
        Assert.AreEqual(-1, occupantPlayerId);
        Assert.IsTrue(_claimPending);
        Assert.IsFalse(_confirmedLocal);
    }

    [TsukimiTest]
    public void LostOwnershipCallbackPreventsReleaseWhileOwnerQueryIsStale()
    {
        var player = Mimic.Join();
        _insideLeft = true;
        occupantPlayerId = player.playerId;
        _ownershipLost = true;
        SetBatterBoxInside(true, false);
        Assert.AreEqual(player.playerId, occupantPlayerId);
        Assert.IsFalse(IsLocalOccupant());
    }


    [TsukimiTest]
    public void AimPointsFollowPlateYawAndZoneDimensions()
    {
        Vector3 origin = new Vector3(5f, 2f, -3f);
        Vector2 offset = new Vector2(0.1f, -0.05f);
        for (int i = 0; i < 4; i++)
        {
            float yaw = i == 3 ? 37f : i * 90f;
            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            {
                Vector3 point = StrikeZoneGeometry.AimPoint(origin, forward, 0.5f, 0.6f,
                    0.4f, 1.3f, offset, column, row);
                Vector3 expected = origin + forward * 0.5f + right * (0.1f + (column - 1) * 0.2f)
                    + Vector3.up * (0.8f + (1 - row) * 0.3f);
                Assert.IsTrue(Vector3.Distance(point, expected) < 0.0001f);
                Assert.IsTrue(Mathf.Abs(Vector3.Dot(point - origin, forward) - 0.5f) < 0.0001f);
            }
        }
    }

    [TsukimiTest]
    public void DefaultZoneUsesMlbAbsHeightsForAn180CmBatter()
    {
        Assert.IsTrue(Mathf.Abs(bottom - 1.8f * 0.27f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(top - 1.8f * 0.535f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(top - bottom - 0.477f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs((top + bottom) * 0.5f - 0.7245f) < 0.00001f);
    }

    [TsukimiTest]
    public void RectangleDistanceIncludesEdgesAndRoundedCorners()
    {
        snapshotOrigin = Vector3.zero;
        snapshotRight = Vector3.right;
        snapshotWidth = 0.4f;
        snapshotBottom = 0.7f;
        snapshotTop = 1.3f;
        Assert.IsTrue(ContainsBall(new Vector3(0f, 1f, 0f), 0.05f));
        Assert.IsFalse(ContainsBall(new Vector3(0.26f, 1f, 0f), 0.05f));
        Assert.IsFalse(ContainsBall(new Vector3(0f, 0.64f, 0f), 0.05f));
        Assert.IsFalse(ContainsBall(new Vector3(0f, 1.36f, 0f), 0.05f));
        Assert.IsTrue(ContainsBall(new Vector3(0.25f, 1f, 0f), 0.051f));
        Assert.IsTrue(ContainsBall(new Vector3(0.23f, 1.34f, 0f), 0.051f));
        Assert.IsFalse(ContainsBall(new Vector3(0.24f, 1.34f, 0f), 0.05f));
    }

    [TsukimiTest]
    public void GeometryUsesWorldHeightAndRotatedHorizontalAxis()
    {
        snapshotOrigin = new Vector3(5f, 2f, 3f);
        snapshotRight = Vector3.back;
        snapshotWidth = 0.4f;
        snapshotBottom = 0.7f;
        snapshotTop = 1.3f;
        Assert.IsTrue(ContainsBall(new Vector3(5f, 3f, 3.1f), 0.03f));
        Assert.IsFalse(ContainsBall(new Vector3(5f, 3f, 3.3f), 0.03f));
        snapshotBottom = 1.1f;
        Assert.IsFalse(ContainsBall(new Vector3(5f, 3f, 3f), 0.03f));
    }

    [TsukimiTest]
    public void ExactOutsideEdgeContactCountsAsStrike()
    {
        snapshotOrigin = Vector3.zero;
        snapshotRight = Vector3.right;
        snapshotWidth = 0.5f;
        snapshotBottom = 0.5f;
        snapshotTop = 1.5f;
        Assert.IsTrue(ContainsBall(new Vector3(0.375f, 1f, 0f), 0.125f));
        Assert.IsFalse(ContainsBall(new Vector3(0.376f, 1f, 0f), 0.125f));
        Assert.IsTrue(ContainsBall(new Vector3(0f, 0.375f, 0f), 0.125f));
        Assert.IsTrue(ContainsBall(new Vector3(0f, 1.625f, 0f), 0.125f));
    }
}
