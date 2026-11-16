using System;
using System.Collections.Generic;
using Engine.Scripts.World;
using Player;
using UnityEngine;
using UnityEngine.UIElements;

namespace UI{
    /// <summary>
    ///     The settings menu (Esc): draw distance with Apply, frame rate, VSync, the debug panels, Resume and Quit.
    ///     The game keeps running while it is open; the player's input is off and the cursor is free.
    /// </summary>
    public partial class HudController{
        private readonly List<int> _drawChoices = new();
        private Button _menuApply;
        private DropdownField _menuDrawDistance;
        private Toggle _menuDebugToggle;
        private DropdownField _menuFrameRate;
        private VisualElement _menu;
        private bool _menuOpen;
        private Toggle _menuVSync;

        /// <summary>The settings menu is open.</summary>
        public bool MenuOpen => _menuOpen;

        private void BindMenu(){
            _menu = Root.Q("menu");
            _menuDrawDistance = Root.Q<DropdownField>("menu-draw-distance");
            _menuApply = Root.Q<Button>("menu-apply");
            _menuFrameRate = Root.Q<DropdownField>("menu-frame-rate");
            _menuVSync = Root.Q<Toggle>("menu-vsync");
            _menuDebugToggle = Root.Q<Toggle>("menu-debug-panels");

            List<string> rates = new();
            foreach (int rate in GameSettings.FrameRates) rates.Add(rate > 0 ? $"{rate} fps" : "Uncapped");
            _menuFrameRate.choices = rates;
            _menuDebugToggle.label = $"Debug panels ({debugPanelsKey})";

            _menuDrawDistance.RegisterValueChangedCallback(OnMenuDrawDistanceChanged);
            _menuFrameRate.RegisterValueChangedCallback(OnMenuFrameRateChanged);
            _menuVSync.RegisterValueChangedCallback(OnMenuVSyncChanged);
            _menuDebugToggle.RegisterValueChangedCallback(OnMenuDebugPanelsChanged);
            _menuApply.clicked += ApplyMenuDrawDistance;
            Root.Q<Button>("menu-resume").clicked += CloseMenu;
            Root.Q<Button>("menu-quit").clicked += Quit;
            _menu.style.display = DisplayStyle.None;
        }

        private void UnbindMenu(){
            if (_menu == null) return;
            if (_menuOpen) SetMenu(false);
            _menuDrawDistance.UnregisterValueChangedCallback(OnMenuDrawDistanceChanged);
            _menuFrameRate.UnregisterValueChangedCallback(OnMenuFrameRateChanged);
            _menuVSync.UnregisterValueChangedCallback(OnMenuVSyncChanged);
            _menuDebugToggle.UnregisterValueChangedCallback(OnMenuDebugPanelsChanged);
            _menuApply.clicked -= ApplyMenuDrawDistance;
            Root.Q<Button>("menu-resume").clicked -= CloseMenu;
            Root.Q<Button>("menu-quit").clicked -= Quit;
        }

        /// <summary>Opens or closes the menu; while it is open the cursor is free and the player's input is off.</summary>
        public void SetMenu(bool open){
            if (_menu == null) return;
            _menuOpen = open;
            _menu.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            _crosshair.style.display = open ? DisplayStyle.None : DisplayStyle.Flex;
            SetPlayerInput(!open);
            if (open) RefreshMenu();
            else _menu.Blur();
        }

        private void CloseMenu(){
            SetMenu(false);
        }

        /// <summary>Shows the current choices: the world's draw distance, the saved frame cap and VSync.</summary>
        private void RefreshMenu(){
            VoxelWorld world = VoxelWorld.Instance;
            int current = world ? world.DrawDistance : 0;
            _drawChoices.Clear();
            _drawChoices.AddRange(GameSettings.DrawDistances);
            if (current > 0 && !_drawChoices.Contains(current)){
                _drawChoices.Add(current);
                _drawChoices.Sort();
            }

            List<string> labels = new(_drawChoices.Count);
            foreach (int distance in _drawChoices) labels.Add($"{distance} chunks");
            _menuDrawDistance.choices = labels;
            _menuDrawDistance.SetValueWithoutNotify(current > 0 ? $"{current} chunks" : "");
            _menuDrawDistance.SetEnabled(world);
            UpdateApplyButton();

            int rateIndex = Array.IndexOf(GameSettings.FrameRates, GameSettings.FrameRate);
            _menuFrameRate.SetValueWithoutNotify(_menuFrameRate.choices[rateIndex >= 0 ? rateIndex : GameSettings.FrameRates.Length - 1]);
            _menuVSync.SetValueWithoutNotify(GameSettings.VSync);
            _menuFrameRate.SetEnabled(!GameSettings.VSync);
            _menuDebugToggle.SetValueWithoutNotify(showDebugPanels);
        }

        /// <summary>The draw distance picked in the menu, 0 when none is.</summary>
        private int PickedDrawDistance(){
            int index = _menuDrawDistance.index;
            return index >= 0 && index < _drawChoices.Count ? _drawChoices[index] : 0;
        }

        private void UpdateApplyButton(){
            VoxelWorld world = VoxelWorld.Instance;
            int picked = PickedDrawDistance();
            _menuApply.SetEnabled(world && picked > 0 && picked != world.DrawDistance);
        }

        private void OnMenuDrawDistanceChanged(ChangeEvent<string> _){
            UpdateApplyButton();
        }

        /// <summary>
        ///     Builds the world again at the picked draw distance around the player, who keeps the pose and is held
        ///     until the ground under them is solid again. The choice is saved for the next start.
        /// </summary>
        private void ApplyMenuDrawDistance(){
            VoxelWorld world = VoxelWorld.Instance;
            int picked = PickedDrawDistance();
            if (!world || picked <= 0) return;
            GameSettings.DrawDistance = picked;
            if (world.ChangeDrawDistance(picked)){
                PlayerManager player = FindFirstObjectByType<PlayerManager>();
                if (player) player.HoldUntilGrounded();
            }

            UpdateApplyButton();
            _nextRefresh = 0f;
        }

        private void OnMenuFrameRateChanged(ChangeEvent<string> _){
            int index = _menuFrameRate.index;
            if (index < 0 || index >= GameSettings.FrameRates.Length) return;
            GameSettings.FrameRate = GameSettings.FrameRates[index];
            GameSettings.ApplyFrameCap();
        }

        private void OnMenuVSyncChanged(ChangeEvent<bool> change){
            GameSettings.VSync = change.newValue;
            GameSettings.ApplyFrameCap();
            _menuFrameRate.SetEnabled(!change.newValue);
        }

        private void OnMenuDebugPanelsChanged(ChangeEvent<bool> change){
            SetDebugPanels(change.newValue);
        }

        /// <summary>Leaves Play mode in the Editor; quits the player otherwise.</summary>
        private static void Quit(){
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
