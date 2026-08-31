using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Player
{
    /// <summary>
    /// Manages switching between walking and flying controllers based on input
    /// and current grounded state.
    /// </summary>
    public class PlayerControllerManager : MonoBehaviour
    {
        /// <summary>
        /// Fly controller used when the player is in flying mode.
        /// </summary>
        [SerializeField] private FlyController flyController;

        /// <summary>
        /// Look controller responsible for camera rotation.
        /// </summary>
        [SerializeField] private LookController lookController;

        /// <summary>
        /// Walking controller used when the player is in walking mode.
        /// </summary>
        [SerializeField] private WalkingController walkingController;

        private CharacterController _characterController;

        public enum MovementMode
        {
            None,
            Walking,
            Flying
        }

        public MovementMode Mode => _mode;

        private MovementMode _mode = MovementMode.Walking;
        private InputAction _jumpInput;
        private bool _crouchPressed;

        private void OnEnable()
        {
            _characterController = GetComponent<CharacterController>();
            SwitchMovementMode(MovementMode.Walking, true);
        }

        private void OnDisable()
        {
            SwitchMovementMode(MovementMode.None, true);
        }

        private void Update()
        {
            if (_mode == MovementMode.Flying && _crouchPressed && _characterController.isGrounded)
            {
                SwitchMovementMode(MovementMode.Walking);
            }
        }

        /// <summary>
        /// Input System callback for the "double jump" action which toggles flying mode
        /// when currently walking.
        /// </summary>
        /// <param name="value">Button state for the double jump action.</param>
        public void OnDoubleJump(InputValue value)
        {
            if (_mode == MovementMode.Walking && !value.isPressed)
            {
                SwitchMovementMode(MovementMode.Flying);
            }
        }

        /// <summary>
        /// Input System callback for crouch, used to return to walking mode when grounded in fly mode.
        /// </summary>
        /// <param name="value">Button state for crouch.</param>
        public void OnCrouch(InputValue value)
        {
            _crouchPressed = value.isPressed;
        }
        
        public void SwitchMovementMode(MovementMode newMode, bool force = false)
        {
            if (_mode == newMode && !force) return;
            _mode = newMode;
            
            _characterController.enabled = _mode != MovementMode.None;
            if(lookController) lookController.enabled = _mode != MovementMode.None;
            if (flyController) flyController.enabled = _mode == MovementMode.Flying;
            if (walkingController) walkingController.enabled = _mode == MovementMode.Walking;
        }
    }
}