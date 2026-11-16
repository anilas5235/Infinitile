using Engine.Scripts.Render.Post;
using UnityEngine;

namespace Engine.Scripts.Diagnostics{
    /// <summary>
    ///     The measurement rig of the frame-time goal: the focus object flies above the terrain without the player,
    ///     looking along the flight. The pipeline tests and the goal run of a player build (<see cref="GoalRunner" />)
    ///     share it, so both measure the same thing. A goal run is a fresh world with the focus standing still for
    ///     <see cref="StandSeconds" /> where the flights start, then one flight along +x per entry of
    ///     <see cref="GoalFlightSpeeds" /> for <see cref="FlightSeconds" />, each followed by the stop until the
    ///     world around the focus is built again. Frames from <see cref="StableAfterSeconds" /> after the start are
    ///     judged against the goal.
    /// </summary>
    internal static class GoalRunRig{
        /// <summary>
        ///     Height of every flight: above the terrain along the path (highest block y 123 to 139 near the spawn)
        ///     and under the world's top at 256.
        /// </summary>
        internal const float FlightHeight = 200f;

        /// <summary>Degrees the camera of a flight looks down, so the picture holds terrain and not only sky.</summary>
        internal const float FlightPitch = 20f;

        /// <summary>Spectator flight with sprint: 40 units per second times two.</summary>
        internal const float SprintFlightSpeed = 80f;

        /// <summary>
        ///     The flights of the goal run, in units per second: the spectator sprint, and the fastest movement
        ///     outside spectator mode (flying at 20 with sprint, or the spectator without).
        /// </summary>
        internal static readonly float[] GoalFlightSpeeds = { SprintFlightSpeed, 40f };

        /// <summary>How long every flight lasts.</summary>
        internal const float FlightSeconds = 30f;

        /// <summary>
        ///     The first world build at the start is exempt from the frame-time goal; from this many seconds after
        ///     the start the frame times must be stable.
        /// </summary>
        internal const float StableAfterSeconds = 20f;

        /// <summary>How long the goal run stands still before the first flight, from the world start.</summary>
        internal const float StandSeconds = 40f;

        /// <summary>Columns around the focus that count as "near" for the meshed share near the focus.</summary>
        internal const int NearRadius = 4;

        /// <summary>The meshed shares of a flight are sampled on every frame whose index is a multiple of this.</summary>
        internal const int CoverageEveryFrames = 15;

        /// <summary>How long a world may take to build, or to settle after a flight, before a run gives up.</summary>
        internal const float BuildTimeoutSeconds = 600f;

        /// <summary>
        ///     What a flight moves: the focus alone, with nothing in the scene that can fall. In the game scene the
        ///     focus is the camera of a player with physics. Moving the camera alone leaves the player behind, the
        ///     ground under it unloads, it falls, and the camera, its child, sinks with it whenever the rig does
        ///     not hold it (between two phases). A measurement of the pipeline needs the focus and the rendering
        ///     camera, not the player: the focus is taken out of the player and the player object is switched off.
        ///     The HUD and the rest of the scene stay as they are.
        ///     The flight goes above the terrain (the spawn is on the sea floor, 46 blocks under water): the focus
        ///     is lifted to <see cref="FlightHeight" /> and looks along the flight, <see cref="FlightPitch" /> degrees
        ///     down. The effect of the water around the spawn would stay on the picture, because the handler that
        ///     follows the camera sits on the player: it is cleared here. Only a reload of the scene gives it a
        ///     player again.
        /// </summary>
        internal static Transform FlightBody(Transform focus){
            if (focus.parent != null){
                GameObject player = focus.root.gameObject;
                focus.SetParent(null, true);
                player.SetActive(false);
                PostProcessManager post = PostProcessManager.Instance;
                if (post){
                    post.ResetLiftGammaGain();
                    post.ResetColorAdjustments();
                }

                UnderwaterFogFeature.Enabled = false;
            }

            Vector3 eye = focus.position;
            focus.SetPositionAndRotation(new Vector3(eye.x, FlightHeight, eye.z), Quaternion.Euler(FlightPitch, 90f, 0f));
            return focus;
        }

        /// <summary>Where a flight along +x at <paramref name="speed" /> is <paramref name="elapsed" /> seconds after its start.</summary>
        internal static Vector3 FlightPosition(Vector3 start, float speed, float elapsed){
            return start + new Vector3(speed * elapsed, 0f, 0f);
        }
    }
}
