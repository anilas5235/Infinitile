using Engine.Scripts.Render;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UI{
    /// <summary>
    ///     Debug keys for the GPU page culling, from the "Hud" map of the project's input actions: ToggleCulling (F8)
    ///     switches culling itself (off = every page with points is drawn, for before/after comparison),
    ///     FreezeCulling (F9) freezes the frustum so you can fly out and look at what was culled. The page counts show
    ///     in the HUD's Culling panel. Game-side only; the engine exposes the switches as properties on
    ///     <see cref="VoxelWorldRenderer" />.
    /// </summary>
    public class CullingDebugKeys : MonoBehaviour{
        private InputAction _toggle, _freeze;

        private void OnEnable(){
            InputActionMap map = HudInput.Map();
            _toggle = map?.FindAction("ToggleCulling");
            _freeze = map?.FindAction("FreezeCulling");
        }

        private void Update(){
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            if (renderer == null) return;

            if (_toggle != null && _toggle.WasPressedThisFrame()) renderer.GpuCulling = !renderer.GpuCulling;
            if (_freeze != null && _freeze.WasPressedThisFrame()) renderer.FreezeCulling = !renderer.FreezeCulling;
        }
    }
}
