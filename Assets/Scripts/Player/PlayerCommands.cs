using Engine.Scripts.Commands;
using UnityEngine;

namespace Player{
    /// <summary>
    ///     The player for console commands: its feet position, and teleporting. A teleport keeps the movement mode;
    ///     a walking player is held at the target until the ground there is loaded
    ///     (<see cref="PlayerManager.HoldUntilGrounded" />), so it does not fall through ground that is not built yet.
    /// </summary>
    public sealed class PlayerCommands : ICommandPlayer{
        private readonly CharacterController _body;
        private readonly PlayerControllerManager _controllers;
        private readonly PlayerManager _manager;

        public PlayerCommands(PlayerManager manager){
            _manager = manager;
            _body = manager.GetComponent<CharacterController>();
            _controllers = manager.GetComponent<PlayerControllerManager>();
        }

        public Vector3 Position => _manager.transform.position + Vector3.up * FeetOffset;

        public void Teleport(Vector3 feet){
            Transform transform = _manager.transform;
            // a character controller that is on puts the body back where it was; move it while it is off
            bool bodyOn = _body && _body.enabled;
            if (bodyOn) _body.enabled = false;
            transform.position = feet - Vector3.up * FeetOffset;
            Physics.SyncTransforms();
            if (bodyOn) _body.enabled = true;

            if (_controllers && _controllers.Mode == PlayerControllerManager.MovementMode.Walking)
                _manager.HoldUntilGrounded();
        }

        /// <summary>Height of the bottom of the body's capsule over the player's position.</summary>
        private float FeetOffset =>
            _body ? (_body.center.y - _body.height * 0.5f) * _manager.transform.lossyScale.y : 0f;
    }
}
