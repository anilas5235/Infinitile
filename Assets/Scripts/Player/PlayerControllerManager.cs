using UnityEngine;
using UnityEngine.InputSystem;

namespace Player{
    /// <summary>
    ///     Manages switching between walking, flying and spectator controllers based on input
    ///     and current grounded state. The mode key cycles walking, flying and spectator; spectator flies through
    ///     blocks with the CharacterController off. The mode survives the player being disabled, e.g. while
    ///     <see cref="PlayerManager.HoldUntilGrounded" /> waits for the ground.
    /// </summary>
    public class PlayerControllerManager : MonoBehaviour{
        public enum MovementMode{
            None,
            Walking,
            Flying,
            Spectator
        }

        [Tooltip("Cycles walking, flying and spectator (free flight through blocks).")]
        [SerializeField] private Key modeKey = Key.G;

        /// <summary>
        ///     Fly controller used when the player is in flying mode.
        /// </summary>
        [SerializeField] private FlyController flyController;

        /// <summary>
        ///     Look controller responsible for camera rotation.
        /// </summary>
        [SerializeField] private LookController lookController;

        /// <summary>
        ///     Walking controller used when the player is in walking mode.
        /// </summary>
        [SerializeField] private WalkingController walkingController;

        private CharacterController _characterController;
        private bool _crouchPressed;
        private InputAction _jumpInput;
        private MovementMode _resumeMode = MovementMode.Walking;

        public MovementMode Mode{ get; private set; } = MovementMode.Walking;

        /// <summary>The key that cycles the movement modes.</summary>
        public Key ModeKey => modeKey;

        private void Update(){
            // The look controller is off while the HUD frees the cursor, e.g. while typing in the voxel search.
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[modeKey].wasPressedThisFrame && lookController && lookController.enabled)
                SwitchMovementMode(Next(Mode));

            if (Mode == MovementMode.Flying && _crouchPressed && _characterController.isGrounded)
                SwitchMovementMode(MovementMode.Walking);
        }

        private void OnEnable(){
            _characterController = GetComponent<CharacterController>();
            SwitchMovementMode(_resumeMode, true);
        }

        private void OnDisable(){
            if (Mode != MovementMode.None) _resumeMode = Mode;
            SwitchMovementMode(MovementMode.None, true);
        }

        private static MovementMode Next(MovementMode mode){
            return mode switch{
                MovementMode.Walking => MovementMode.Flying,
                MovementMode.Flying => MovementMode.Spectator,
                _ => MovementMode.Walking
            };
        }

        /// <summary>
        ///     Input System callback for the "double jump" action which toggles flying mode
        ///     when currently walking.
        /// </summary>
        /// <param name="value">Button state for the double jump action.</param>
        public void OnDoubleJump(InputValue value){
            if (Mode == MovementMode.Walking && !value.isPressed) SwitchMovementMode(MovementMode.Flying);
        }

        /// <summary>
        ///     Input System callback for crouch, used to return to walking mode when grounded in fly mode.
        /// </summary>
        /// <param name="value">Button state for crouch.</param>
        public void OnCrouch(InputValue value){
            _crouchPressed = value.isPressed;
        }

        public void SwitchMovementMode(MovementMode newMode, bool force = false){
            if (Mode == newMode && !force) return;
            Mode = newMode;

            _characterController.enabled = Mode is MovementMode.Walking or MovementMode.Flying;
            if (lookController) lookController.enabled = Mode != MovementMode.None;
            if (flyController){
                flyController.PassThrough = Mode == MovementMode.Spectator;
                flyController.enabled = Mode is MovementMode.Flying or MovementMode.Spectator;
            }

            if (walkingController) walkingController.enabled = Mode == MovementMode.Walking;
        }
    }
}
