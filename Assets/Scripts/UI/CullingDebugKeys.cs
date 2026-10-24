using Engine.Scripts.Render;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UI{
    /// <summary>
    ///     Debug keys for the GPU page culling: F8 toggles culling itself (off = every page with points is drawn,
    ///     for before/after comparison), F9 freezes the frustum so you can fly out and look at what was culled. The
    ///     page counts show in the HUD's performance panel. Game-side only; the engine exposes the switches as
    ///     properties on <see cref="VoxelWorldRenderer" />.
    /// </summary>
    public class CullingDebugKeys : MonoBehaviour{
        [SerializeField] private Key cullingKey = Key.F8;
        [SerializeField] private Key freezeKey = Key.F9;

        private void Update(){
            Keyboard keyboard = Keyboard.current;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            if (keyboard == null || renderer == null) return;

            if (keyboard[cullingKey].wasPressedThisFrame) renderer.GpuCulling = !renderer.GpuCulling;
            if (keyboard[freezeKey].wasPressedThisFrame) renderer.FreezeCulling = !renderer.FreezeCulling;
        }
    }
}
