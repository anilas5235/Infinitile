using UnityEngine;
using UnityEngine.InputSystem;

namespace Player{
    /// <summary>
    /// Handles first-person style camera look using mouse or gamepad input,
    /// rotating the target camera and player transform.
    /// </summary>
    public class LookController : MonoBehaviour{
        private Vector2 _lookInput;
        private float _pitch; // vertical rotation

        /// <summary>
        /// Sensitivity multiplier for look input.
        /// </summary>
        [Range(1f, 100f)] public float lookSpeed = 10f;

        /// <summary>
        /// Maximum absolute pitch angle in degrees.
        /// </summary>
        [Range(1f, 89f)] public float maxLookAngle = 80f;

        [SerializeField] private Camera targetCamera;

        private void Start(){
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        /// <summary>
        /// Input System callback for look input (mouse delta / right stick).
        /// </summary>
        /// <param name="value">2D look delta.</param>
        public void OnLook(InputValue value){
            _lookInput = value.Get<Vector2>();
        }

        private void Update(){
            if (!(_lookInput.sqrMagnitude > 0.0001f)) return;

            // Yaw (horizontal)
            //targetCamera.transform.Rotate(Vector3.up, _lookInput.x * lookSpeed * Time.deltaTime, Space.World);

            // Pitch (vertical) mit Clamp
            _pitch -= _lookInput.y * lookSpeed * Time.deltaTime;
            _pitch = Mathf.Clamp(_pitch, -maxLookAngle, maxLookAngle);

            Vector3 currentEuler = targetCamera.transform.eulerAngles;
            targetCamera.transform.eulerAngles = new Vector3(_pitch, currentEuler.y, 0f);
            transform.Rotate(Vector3.up * (_lookInput.x * lookSpeed * Time.deltaTime));
        }
    }
}
