using System.Drawing;

namespace IndepenDesk;

/// <summary>Outcome of one topology-placement materialization attempt (§12, §13 of
/// docs/taskbar-topology-return-fix-plan.md): only <c>Failed</c> is retriable.</summary>
internal enum TopologyPlacementOutcome { Applied, Failed, Destroyed, IdentityMismatch }

/// <summary>
/// Pure decision functions of the taskbar/topology-placement fix. They encode the
/// decision matrix of the fix plan (§9 jump, §10 unpark, §11 sync adoption and
/// retry) without touching Win32: callers own every fact (window identity, physical
/// monitor, journal target) and the functions only choose between the documented
/// outcomes — Jump / Adopt / Repark / RetryPlacement / Ignore — so the rules can be
/// unit-tested without Win32 mocks.
/// </summary>
internal static class TopologyDecisions
{
    /// <summary>Shared "same rectangle" tolerance (parking verification uses it too).</summary>
    internal const int RectTolerance = 2;

    // ---------- §9: default (hidden) mode taskbar jump ----------

    internal enum HiddenTaskbarJump { Jump, LeaveForSync, Ignore }

    /// <summary>
    /// A re-shown hidden window was activated from the taskbar. The jump goes to the
    /// desktop that owns the window when the window physically sits on that monitor,
    /// or when a pending topology placement targets it (the journal is authoritative;
    /// the stale physical position must not veto the jump). Without a pending, an
    /// app-initiated cross-display move is left for Sync adoption as before.
    /// </summary>
    internal static HiddenTaskbarJump DecideHiddenTaskbarJump(
        bool ownerIsCurrent,
        string? physicalDevice,
        string? ownerDevice,
        string? pendingTargetDevice)
    {
        if (ownerIsCurrent) return HiddenTaskbarJump.Ignore;
        if (DevicesEqual(physicalDevice, ownerDevice)) return HiddenTaskbarJump.Jump;
        if (pendingTargetDevice != null && DevicesEqual(pendingTargetDevice, ownerDevice))
            return HiddenTaskbarJump.Jump;
        return HiddenTaskbarJump.LeaveForSync;
    }

    // ---------- §11: Sync adoption of a window with a pending placement ----------

    internal enum SyncPending { SuppressAdoption, ProceedNormally }

    /// <summary>
    /// A visible window reaches the Sync adoption pass with a pending placement: a
    /// matching pending suppresses the adoption (the temporary physical display must
    /// not move membership); a reused HWND drops the stale pending and adopts normally.
    /// </summary>
    internal static SyncPending DecideSyncPending(bool pendingExists, bool identityMatches) =>
        pendingExists && identityMatches ? SyncPending.SuppressAdoption : SyncPending.ProceedNormally;

    // ---------- §10: shared-mode unpark of an already on-screen window ----------

    internal enum UnparkEarlyReturn { AdoptOnScreenPosition, RestoreToRecordedTarget }

    /// <summary>
    /// A parked window is back on-screen before its unpark: normally that means the
    /// app restored it itself (adopt the on-screen position), but with a pending
    /// topology placement the record's target display is still authoritative.
    /// </summary>
    internal static UnparkEarlyReturn DecideUnparkEarlyReturn(bool pendingExists) =>
        pendingExists ? UnparkEarlyReturn.RestoreToRecordedTarget : UnparkEarlyReturn.AdoptOnScreenPosition;

    // ---------- §11: visibility after a pending placement landed ----------

    internal enum PendingLandedVisibility { ShowWindow, RehideOrRepark, LeaveAsIs }

    /// <summary>
    /// A pending placement just materialized: the owner desktop being presented means
    /// the window must be visible; a re-shown window of a non-current desktop is
    /// re-hidden/re-parked now that it sits on the correct display; hidden and parked
    /// representations are already correct.
    /// </summary>
    internal static PendingLandedVisibility DecidePendingLandedVisibility(
        bool hasOwner, bool ownerIsCurrent, bool windowVisible, bool parkedRepresentation)
    {
        if (hasOwner && ownerIsCurrent) return PendingLandedVisibility.ShowWindow;
        if (hasOwner && !ownerIsCurrent && windowVisible && !parkedRepresentation)
            return PendingLandedVisibility.RehideOrRepark;
        return PendingLandedVisibility.LeaveAsIs;
    }

    // ---------- §8.1: periodic retry gating ----------

    internal enum PendingRetry { Retry, SkipThrottled, DropStale }

    /// <summary>
    /// One pending placement's entry in the periodic retry pass. There is
    /// deliberately no topology-disturbance expiry: a pending ends only with a
    /// verified placement or a dead/reused HWND; between attempts the physical retry
    /// is throttled so a refusing window cannot block the UI thread every second.
    /// </summary>
    internal static PendingRetry DecidePendingRetry(
        bool windowAlive, bool identityMatches, long now, long retryNotBefore)
    {
        if (!windowAlive || !identityMatches) return PendingRetry.DropStale;
        if (now < retryNotBefore) return PendingRetry.SkipThrottled;
        return PendingRetry.Retry;
    }

    // ---------- pending lifecycle ----------

    /// <summary>
    /// A journal rewrite withdraws a pending placement when the record no longer
    /// targets the same display/rectangle the pending was created for (the window is
    /// being managed towards a new target; the old placement is not "pending" anymore).
    /// </summary>
    internal static bool ShouldWithdrawPending(
        string? pendingDevice, Rectangle pendingRect, string? recordDevice, Rectangle recordRect)
    {
        if (!DevicesEqual(pendingDevice, recordDevice)) return true;
        return Math.Abs(pendingRect.Left - recordRect.Left) > RectTolerance ||
               Math.Abs(pendingRect.Top - recordRect.Top) > RectTolerance ||
               Math.Abs(pendingRect.Right - recordRect.Right) > RectTolerance ||
               Math.Abs(pendingRect.Bottom - recordRect.Bottom) > RectTolerance;
    }

    /// <summary>
    /// A failed placement is only retriable while the same window still lives behind
    /// the HWND; a destroyed or reused handle is a terminal outcome that cleans up
    /// every trace of the old window instead of retrying.
    /// </summary>
    internal static TopologyPlacementOutcome ClassifyPlacementFailure(
        bool windowAlive, bool identityMatches)
    {
        if (!windowAlive) return TopologyPlacementOutcome.Destroyed;
        if (!identityMatches) return TopologyPlacementOutcome.IdentityMismatch;
        return TopologyPlacementOutcome.Failed;
    }

    private static bool DevicesEqual(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
