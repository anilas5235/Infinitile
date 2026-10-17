using UnityEngine;
using UnityEngine.InputSystem;

namespace Player{
    /// <summary>
    ///     Simple free-fly character controller driven by the Unity Input System,
    ///     moving relative to a target camera orientation.
    /// </summary>
    public class FlyController : MonoBehaviour{
        /// <summary>
        ///     Base movement speed in units per second.
        /// </summary>
        [Range(1f, 100f)] public float moveSpeed = 20f;

        [SerializeField] private Camera targetCamera;
        private CharacterController _controller;
        private bool _crouchInput;

        private bool _jumpInput;
        private Vector2 _moveInput;
        private float _pitch; // vertical rotation
        private bool _sprintInput;
        private float _verticalInput; // for up/down movement

        private void Update(){
            _verticalInput = 0f;
            if (_jumpInput) _verticalInput += 1f;
            if (_crouchInput) _verticalInput -= 1f;
            float speedModifier = 1f;
            if (_sprintInput) speedModifier = 2f; // Shift

            // Create movement vector relative to camera orientation
            Vector3 forward = targetCamera.transform.forward;
            Vector3 right = targetCamera.transform.right;

            // Normalize to prevent faster diagonal movement
            if (forward.magnitude > 0.01f) forward.Normalize();
            if (right.magnitude > 0.01f) right.Normalize();
            // Create movement direction
            Vector3 moveDirection = forward * _moveInput.y + right * _moveInput.x;

            // Add vertical movement
            moveDirection.y = _verticalInput;

            _controller.Move(moveDirection.normalized * (moveSpeed * speedModifier * Time.deltaTime));
        }

        private void OnEnable(){
            _controller = GetComponent<CharacterController>();
        }

        /// <summary>
        ///     Input System callback for movement input (WASD / left stick).
        /// </summary>
        /// <param name="value">2D movement vector.</param>
        public void OnMove(InputValue value){
            _moveInput = value.Get<Vector2>();
        }

        /// <summary>
        ///     Input System callback for sprint toggle.
        /// </summary>
        /// <param name="value">Button state indicating whether sprint is active.</param>
        public void OnSprint(InputValue value){
            _sprintInput = value.isPressed;
        }

        /// <summary>
        ///     Input System callback for jump (ascend) in fly mode.
        /// </summary>
        /// <param name="value">Button state indicating whether ascend is active.</param>
        public void OnJump(InputValue value){
            _jumpInput = value.isPressed;
        }

        /// <summary>
        ///     Input System callback for crouch (descend) in fly mode.
        /// </summary>
        /// <param name="value">Button state indicating whether descend is active.</param>
        public void OnCrouch(InputValue value){
            _crouchInput = value.isPressed;
        }
    }
}
