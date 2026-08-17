using Multiplayer;
using TwilightCore.Match;
using UnityEngine;

namespace TwilightCore.Physics;

/// <summary>
/// Menu fall-speed limiter: while connected to the match server AND sitting in
/// the main menu, clamp the player ragdoll's downward velocity so it can't
/// build up lethal fall speed off the menu scenery. Purely local behaviour —
/// no messages involved.
///
/// The clamp is re-evaluated every physics frame from the authoritative state
/// (MatchSession.IsAuthenticated / App.state) instead of being toggled on
/// connect/disconnect events, so leaving the menu, loading a level (including
/// the 'Empty' transition scene) or dropping the connection stops it on the
/// very next frame with nothing to restore.
/// </summary>
internal class MenuFallSpeedLimiter : MonoBehaviour
{
    /// <summary>Maximum downward velocity (m/s) while the limiter is active.</summary>
    public const float MaxFallSpeed = 50f;

    private void FixedUpdate()
    {
        if (!TwilightConfig.EnableMenuFallLimit.Value) return;

        // Only while authenticated with the match server.
        var session = MatchSession.Instance;
        if (session == null || !session.IsAuthenticated) return;

        // Only in the main menu — entering/loading a level (and the 'Empty'
        // transition) flips App.state away from Menu first, so those are
        // excluded automatically. Customize is deliberately not covered.
        if (App.state != AppSate.Menu) return;

        var human = Human.Localplayer;
        if (human == null || !human) return; // Unity fake-null after a scene change

        // Clamp each rigidbody's downward velocity separately: preserves the
        // horizontal components and the per-body differences of the ragdoll,
        // and only touches fall speed (unlike Human.ControlVelocity, which
        // clamps the whole velocity magnitude).
        var bodies = human.rigidbodies;
        if (bodies == null) return;
        for (int i = 0; i < bodies.Length; i++)
        {
            var rb = bodies[i];
            if (rb == null) continue;
            var v = rb.velocity;
            if (v.y < -MaxFallSpeed)
                rb.velocity = new Vector3(v.x, -MaxFallSpeed, v.z);
        }
    }
}
