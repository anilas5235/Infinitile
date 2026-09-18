using UnityEngine;
using UnityEngine.InputSystem;

namespace Player{
    /// <summary>
    /// Character controller that handles grounded walking, sprinting and jumping
    /// using the Unity CharacterController component.
    /// </summary>
    public class WalkingController : MonoBehaviour{
        private Vector2 _moveInput;
        private bool _sprintInput;

        /// <summary>
        /// Base walking speed in units per second.
        /// </summary>
        [Range(1f, 100f)] public float moveSpeed = 20f;

        /// <summary>
        /// Jump height in world units.
        /// </summary>
        [Range(1f, 20f)] public float jumpHeight = 1.5f;

        /// <summary>
        /// Gravity value applied to the character (negative for downward acceleration).
        /// </summary>
        public float gravity = -9.81f;

        private CharacterController _controller;
        [SerializeField] private Camera targetCamera;

        private bool _jumpInput;

        private Vector3 _velocity;

        /// <summary>
        /// Indicates whether the character is currently grounded.
        /// </summary>
        public bool isGrounded;

        private void OnEnable(){
            _controller = GetComponent<CharacterController>();
        }

        /// <summary>
        /// Input System callback for movement input (WASD / left stick).
        /// </summary>
        /// <param name="value">2D movement vector.</param>
        public void OnMove(InputValue value){
            _moveInput = value.Get<Vector2>();
        }

        /// <summary>
        /// Input System callback for sprint toggle.
        /// </summary>
        /// <param name="value">Button state indicating whether sprint is active.</param>
        public void OnSprint(InputValue value){
            _sprintInput = value.isPressed;
        }

        /// <summary>
        /// Input System callback for jump.
        /// </summary>
        /// <param name="value">Button state indicating whether jump is pressed.</param>
        public void OnJump(InputValue value){
            _jumpInput = value.isPressed;
        }

        private void Update(){
            float speedModifier = _sprintInput ? 1.5f : 1.0f;
            isGrounded = _controller.isGrounded;

            // Create movement vector relative to camera orientation
            Vector3 forward = transform.forward;
            Vector3 right = transform.right;

            // Normalize to prevent faster diagonal movement
            if (forward.magnitude > 0.01f) forward.Normalize();
            if (right.magnitude > 0.01f) right.Normalize();
            // Create movement direction
            Vector3 moveDirection = forward * _moveInput.y + right * _moveInput.x;

            _controller.Move(moveDirection.normalized * (moveSpeed * speedModifier * Time.deltaTime));

            // Handle jumping
            if (isGrounded){
                // Apply gravity
                if (_velocity.y < 0) _velocity.y = -2f; // small downward force to keep grounded

                if (_jumpInput) _velocity.y = Mathf.Sqrt(2f * -gravity * jumpHeight);
            }

            _velocity.y += gravity * Time.deltaTime;
            _controller.Move(_velocity * Time.deltaTime);
        }
    }
}
