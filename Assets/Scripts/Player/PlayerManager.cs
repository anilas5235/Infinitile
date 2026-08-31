using Engine.Scripts.World;
using Engine.Shaders;
using UnityEngine;

namespace Player
{
    public class PlayerManager : MonoBehaviour
    {
        private PlayerControllerManager _controllerManager;
        private VoxelEditor _voxelEditor;
        private VoxelPostProcessHandler _postProcessHandler;

        private bool _playerEnabled = true;

        private void OnEnable()
        {
            _controllerManager = GetComponent<PlayerControllerManager>();
            _voxelEditor = GetComponent<VoxelEditor>();
            _postProcessHandler = GetComponent<VoxelPostProcessHandler>();
        }

        private void Start()
        {
            DisablePlayer();
        }

        private void FixedUpdate()
        {
            if (!_playerEnabled && VoxelWorld.Instance.ReadyForPlayer())
            {
                EnablePlayer();
            }
        }

        public void DisablePlayer()
        {
            if (!_playerEnabled) return;
            _playerEnabled = false;
            _controllerManager.enabled = false;
            _voxelEditor.enabled = false;
            _postProcessHandler.enabled = false;
        }

        public void EnablePlayer()
        {
            if (_playerEnabled) return;
            _playerEnabled = true;
            _controllerManager.enabled = true;
            _voxelEditor.enabled = true;
            _postProcessHandler.enabled = true;
        }
    }
}