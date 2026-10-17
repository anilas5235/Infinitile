using UnityEngine;
using UnityEngine.InputSystem;

namespace Player{
    /// <summary>
    ///     Manages switching between walking and flying controllers based on input
    ///     and current grounded state.
    /// </summary>
    public class PlayerControllerManager : MonoBehaviour{
        public enum MovementMode{
            None,
            Walking,
            Flying
        }

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

        public MovementMode Mode{ get; private set; } = MovementMode.Walking;

        private void Update(){
            if (Mode == MovementMode.Flying && _crouchPressed && _characterController.isGrounded)
                SwitchMovementMode(MovementMode.Walking);
        }

        private void OnEnable(){
            _characterController = GetComponent<CharacterController>();
            SwitchMovementMode(MovementMode.Walking, true);
        }

        private void OnDisable(){
            SwitchMovementMode(MovementMode.None, true);
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

            _characterController.enabled = Mode != MovementMode.None;
            if (lookController) lookController.enabled = Mode != MovementMode.None;
            if (flyController) flyController.enabled = Mode == MovementMode.Flying;
            if (walkingController) walkingController.enabled = Mode == MovementMode.Walking;
        }
    }
}
