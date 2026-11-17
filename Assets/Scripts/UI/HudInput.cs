using UnityEngine.InputSystem;

namespace UI{
    /// <summary>
    ///     The "Hud" map of the project-wide input actions (InputSystem_Actions): panels, inventory (with its gamepad
    ///     actions), menu, hotbar and the culling debug keys. Unlike the "Player" map it stays on while the menu or
    ///     the inventory is open, so the key that closes it still works.
    /// </summary>
    public static class HudInput{
        /// <summary>The "Hud" action map, enabled; null when the project has no project-wide actions with that map.</summary>
        public static InputActionMap Map(){
            InputActionMap map = InputSystem.actions ? InputSystem.actions.FindActionMap("Hud") : null;
            if (map is{ enabled: false }) map.Enable();
            return map;
        }

        /// <summary>The key or button of an action as the player reads it ("F3", "Esc"), "" without a binding.</summary>
        public static string KeyOf(InputAction action){
            if (action == null) return "";
            string text = action.GetBindingDisplayString(InputBinding.DisplayStringOptions.DontIncludeInteractions);
            int alternative = text.IndexOf(" | ", System.StringComparison.Ordinal);
            if (alternative >= 0) text = text[..alternative];
            return text switch{
                "Escape" => "Esc",
                _ => text
            };
        }
    }
}
