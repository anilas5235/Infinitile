using UnityEngine;
using UnityEngine.InputSystem;

namespace Player{
    /// <summary>
    ///     Handles first-person style camera look using mouse or gamepad input,
    ///     rotating the target camera and player transform.
    /// </summary>
    public class LookController : MonoBehaviour{
        /// <summary>
        ///     Sensitivity multiplier for look input.
        /// </summary>
        [Range(1f, 100f)] public float lookSpeed = 10f;

        /// <summary>
        ///     Maximum absolute pitch angle in degrees.
        /// </summary>
        [Range(1f, 89f)] public float maxLookAngle = 80f;

        [SerializeField] private Camera targetCamera;
        private Vector2 _lookInput;
        private float _pitch; // vertical rotation

        private void Start(){
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update(){
            if (!(_lookInput.sqrMagnitude > 0.0001f)) return;

            // Pitch (vertical, clamped) turns the camera, yaw (horizontal) turns the player
            _pitch -= _lookInput.y * lookSpeed * Time.deltaTime;
            _pitch = Mathf.Clamp(_pitch, -maxLookAngle, maxLookAngle);

            Vector3 currentEuler = targetCamera.transform.eulerAngles;
            targetCamera.transform.eulerAngles = new Vector3(_pitch, currentEuler.y, 0f);
            transform.Rotate(Vector3.up * (_lookInput.x * lookSpeed * Time.deltaTime));
        }

        /// <summary>
        ///     Current camera pitch in degrees.
        /// </summary>
        public float Pitch => _pitch;

        /// <summary>
        ///     Sets the camera pitch in degrees (clamped), e.g. when a camera bookmark is restored.
        /// </summary>
        public void SetPitch(float pitch){
            _pitch = Mathf.Clamp(pitch, -maxLookAngle, maxLookAngle);
            Vector3 currentEuler = targetCamera.transform.eulerAngles;
            targetCamera.transform.eulerAngles = new Vector3(_pitch, currentEuler.y, 0f);
        }

        /// <summary>
        ///     Input System callback for look input (mouse delta / right stick).
        /// </summary>
        /// <param name="value">2D look delta.</param>
        public void OnLook(InputValue value){
            _lookInput = value.Get<Vector2>();
        }
    }
}
