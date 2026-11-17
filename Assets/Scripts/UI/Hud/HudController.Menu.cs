using System;
using System.Collections.Generic;
using Engine.Scripts.Render;
using Engine.Scripts.World;
using UnityEngine;
using UnityEngine.UIElements;
using static System.FormattableString;
using Object = UnityEngine.Object;

namespace UI{
    /// <summary>
    ///     The Esc menu: a main page (Continue, Settings, Quit) and a settings page (draw distance with Apply and the
    ///     GPU store it needs, VSync, frame rate). The game keeps running while it is open; the player's input is off,
    ///     the cursor is free and the panels and hotbar are hidden. The menu key goes back from the settings page,
    ///     and closes the menu from the main page.
    /// </summary>
    public partial class HudController{
        private static readonly Color Ink = new Color32(42, 26, 0, 255);
        private static readonly Color Paper = new Color32(244, 241, 234, 255);
        private static readonly Color ExitRed = new Color32(255, 138, 122, 255);
        private static readonly Color ApplyInk = new Color32(23, 58, 16, 255);
        private static readonly Color Accent = new Color32(242, 179, 61, 255);

        /// <summary>The name on tilted tiles in the world's colours: letter, face, bottom edge, text, tilt, lift.</summary>
        private static readonly (char Letter, Color32 Face, Color32 Edge, Color32 Text, float Tilt, float Lift)[] Logo = {
            ('I', new Color32(108, 195, 90, 255), new Color32(62, 127, 51, 255), new Color32(23, 58, 16, 255), -4f, 0f),
            ('N', new Color32(226, 210, 156, 255), new Color32(168, 149, 94, 255), new Color32(74, 61, 23, 255), 3f, -6f),
            ('F', new Color32(158, 162, 168, 255), new Color32(95, 98, 104, 255), new Color32(36, 39, 44, 255), -2f, 0f),
            ('I', new Color32(93, 147, 208, 255), new Color32(35, 74, 120, 255), new Color32(14, 36, 64, 255), 5f, -10f),
            ('N', new Color32(238, 242, 245, 255), new Color32(174, 184, 194, 255), new Color32(58, 69, 82, 255), -3f, 0f),
            ('I', new Color32(160, 124, 78, 255), new Color32(94, 68, 41, 255), new Color32(42, 28, 12, 255), 2f, -4f),
            ('T', new Color32(242, 179, 61, 255), new Color32(185, 127, 22, 255), new Color32(42, 26, 0, 255), -5f, -12f),
            ('I', new Color32(108, 195, 90, 255), new Color32(62, 127, 51, 255), new Color32(23, 58, 16, 255), 4f, 0f),
            ('L', new Color32(158, 162, 168, 255), new Color32(95, 98, 104, 255), new Color32(36, 39, 44, 255), -2f, -6f),
            ('E', new Color32(226, 210, 156, 255), new Color32(168, 149, 94, 255), new Color32(74, 61, 23, 255), 3f, 0f)
        };

        private readonly List<int> _drawChoices = new();
        private readonly List<Button> _drawTiles = new();
        private readonly List<Button> _rateTiles = new();
        private Texture2D _dimTexture;
        private VisualElement _menu, _menuMain, _menuSettings, _drawTileRow, _rateTileRow;
        private Button _menuApply, _menuContinue, _menuSettingsButton, _menuQuit, _menuVSync, _settingsBack;
        private Label _menuStoreEstimate;
        private bool _menuOpen, _settingsPageOpen;
        private int _pickedDraw;

        /// <summary>The Esc menu is open (either page).</summary>
        public bool MenuOpen => _menuOpen;

        private void BindMenu(){
            _menu = Root.Q("menu");
            _menuMain = Root.Q("menu-main");
            _menuSettings = Root.Q("menu-settings-page");
            _menuContinue = Root.Q<Button>("menu-continue");
            _menuSettingsButton = Root.Q<Button>("menu-settings");
            _menuQuit = Root.Q<Button>("menu-quit");
            _settingsBack = Root.Q<Button>("settings-back");
            _menuApply = Root.Q<Button>("menu-apply");
            _menuVSync = Root.Q<Button>("menu-vsync");
            _menuStoreEstimate = Root.Q<Label>("menu-store-estimate");
            _drawTileRow = Root.Q("menu-draw-distance");
            _rateTileRow = Root.Q("menu-frame-rate");

            BuildLogo(Root.Q("menu-logo"));
            _dimTexture = DimGradient();
            Root.Q("menu-dim").style.backgroundImage = _dimTexture;
            AddGlyph("menu-continue-icon", GlyphKind.Play, Ink);
            AddGlyph("menu-settings-icon", GlyphKind.Gear, Paper);
            AddGlyph("menu-quit-icon", GlyphKind.Exit, ExitRed);
            AddGlyph("settings-back-icon", GlyphKind.Back, Paper);
            AddGlyph("menu-apply-icon", GlyphKind.Check, ApplyInk);
            AddGlyph("menu-warn-icon", GlyphKind.Warning, Accent);

            _rateTileRow.Clear();
            _rateTiles.Clear();
            foreach (int rate in GameSettings.FrameRates){
                int value = rate;
                Button tile = Tile(rate > 0 ? $"{rate}" : "Uncapped", () => PickFrameRate(value));
                _rateTileRow.Add(tile);
                _rateTiles.Add(tile);
            }

            // Without Unity's button look: the blocks style themselves, hover and press included.
            _menu.Query<Button>().ForEach(button => button.RemoveFromClassList(Button.ussClassName));

            _menuContinue.clicked += CloseMenu;
            _menuSettingsButton.clicked += OpenSettingsPage;
            _menuQuit.clicked += Quit;
            _settingsBack.clicked += OpenMainPage;
            _menuApply.clicked += ApplyMenuDrawDistance;
            _menuVSync.clicked += ToggleVSync;
            _menu.style.display = DisplayStyle.None;
        }

        private void UnbindMenu(){
            if (_menu == null) return;
            if (_menuOpen) SetMenu(false);
            _menuContinue.clicked -= CloseMenu;
            _menuSettingsButton.clicked -= OpenSettingsPage;
            _menuQuit.clicked -= Quit;
            _settingsBack.clicked -= OpenMainPage;
            _menuApply.clicked -= ApplyMenuDrawDistance;
            _menuVSync.clicked -= ToggleVSync;
            if (_dimTexture) Object.Destroy(_dimTexture);
        }

        private void BuildLogo(VisualElement logo){
            logo.Clear();
            foreach (var tile in Logo){
                VisualElement face = new(){ pickingMode = PickingMode.Ignore };
                face.AddToClassList("logo__tile");
                face.style.backgroundColor = (Color)tile.Face;
                face.style.borderBottomColor = (Color)tile.Edge;
                face.style.rotate = new Rotate(new Angle(tile.Tilt, AngleUnit.Degree));
                face.style.translate = new Translate(0, tile.Lift);
                Label letter = new(tile.Letter.ToString()){ pickingMode = PickingMode.Ignore };
                letter.AddToClassList("logo__letter");
                letter.style.color = (Color)tile.Text;
                face.Add(letter);
                logo.Add(face);
            }
        }

        /// <summary>Dark on the left, where the menu sits, fading towards the right of the screen.</summary>
        private static Texture2D DimGradient(){
            const int width = 64;
            Texture2D texture = new(width, 1, TextureFormat.RGBA32, false){
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
            };
            Color32[] pixels = new Color32[width];
            for (int x = 0; x < width; x++){
                float t = x / (width - 1f);
                pixels[x] = new Color32(8, 10, 14, (byte)(255 * Mathf.Lerp(0.78f, 0.22f, t)));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void AddGlyph(string well, GlyphKind kind, Color color){
            VisualElement element = Root.Q(well);
            element.Clear();
            element.Add(new HudGlyph(kind, color));
        }

        private static Button Tile(string text, Action clicked){
            Button tile = new(clicked){ text = text };
            tile.RemoveFromClassList(Button.ussClassName);
            tile.AddToClassList("t");
            return tile;
        }

        // ── Open, close, pages ───────────────────────────────────────────────

        /// <summary>Opens the menu on its main page, or closes it.</summary>
        public void SetMenu(bool open){
            if (_menu == null) return;
            if (open && _searchOpen) SetVoxelSearch(false);
            _menuOpen = open;
            _menu.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            SetPlayerInput(!open);
            UpdateLayers();
            if (open) OpenMainPage();
            else _menu.Blur();
        }

        /// <summary>The menu key: back from the settings page, else open or close the menu.</summary>
        private void MenuBack(){
            if (_menuOpen && _settingsPageOpen) OpenMainPage();
            else SetMenu(!_menuOpen);
        }

        private void CloseMenu(){
            SetMenu(false);
        }

        private void OpenMainPage(){
            _settingsPageOpen = false;
            Show(_menuMain, true);
            Show(_menuSettings, false);
        }

        private void OpenSettingsPage(){
            _settingsPageOpen = true;
            Show(_menuMain, false);
            Show(_menuSettings, true);
            RefreshSettings();
        }

        // ── Settings page ────────────────────────────────────────────────────

        /// <summary>Shows the current choices: the world's draw distance, VSync and the saved frame cap.</summary>
        private void RefreshSettings(){
            VoxelWorld world = VoxelWorld.Instance;
            int current = world ? world.DrawDistance : 0;
            _drawChoices.Clear();
            _drawChoices.AddRange(GameSettings.DrawDistances);
            if (current > 0 && !_drawChoices.Contains(current)){
                _drawChoices.Add(current);
                _drawChoices.Sort();
            }

            _drawTileRow.Clear();
            _drawTiles.Clear();
            foreach (int distance in _drawChoices){
                int value = distance;
                Button tile = Tile($"{distance}", () => PickDrawDistance(value));
                if (distance == current){
                    Label badge = new("NOW"){ pickingMode = PickingMode.Ignore };
                    badge.AddToClassList("badge");
                    tile.Add(badge);
                }

                _drawTileRow.Add(tile);
                _drawTiles.Add(tile);
            }

            _drawTileRow.SetEnabled(world);
            _pickedDraw = current;
            RefreshDrawTiles();
            RefreshDisplaySettings();
        }

        private void PickDrawDistance(int distance){
            _pickedDraw = distance;
            RefreshDrawTiles();
        }

        private void RefreshDrawTiles(){
            for (int i = 0; i < _drawTiles.Count; i++) _drawTiles[i].EnableInClassList("t--on", _drawChoices[i] == _pickedDraw);
            VoxelWorld world = VoxelWorld.Instance;
            _menuApply.SetEnabled(world && _pickedDraw > 0 && _pickedDraw != world.DrawDistance);
            RefreshStoreEstimate();
        }

        /// <summary>The GPU interval store now and at the picked draw distance, from the store's own sizing.</summary>
        private void RefreshStoreEstimate(){
            if (_menuStoreEstimate == null) return;
            VoxelWorld world = VoxelWorld.Instance;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            if (!world || !renderer || _pickedDraw <= 0){
                _menuStoreEstimate.text = "";
                return;
            }

            float now = Mb(renderer.GpuMemory.IntervalStoreBytes);
            _menuStoreEstimate.text = _pickedDraw == world.DrawDistance
                ? Invariant($"GPU interval store {now:F0} MB")
                : Invariant($"GPU interval store {now:F0} to {Mb(VoxelWorldRenderer.IntervalStoreBytesAt(_pickedDraw)):F0} MB");
        }

        /// <summary>
        ///     Builds the world again at the picked draw distance around the player, who is frozen in place until the
        ///     ground under them has its collider again (the player listens to the world for that). The choice is
        ///     saved for the next start.
        /// </summary>
        private void ApplyMenuDrawDistance(){
            VoxelWorld world = VoxelWorld.Instance;
            int picked = _pickedDraw;
            if (!world || picked <= 0 || picked == world.DrawDistance) return;
            GameSettings.DrawDistance = picked;
            world.ChangeDrawDistance(picked);
            RefreshSettings();
            _nextRefresh = 0f;
        }

        private void ToggleVSync(){
            GameSettings.VSync = !GameSettings.VSync;
            GameSettings.ApplyFrameCap();
            RefreshDisplaySettings();
        }

        private void PickFrameRate(int rate){
            if (GameSettings.VSync) return;
            GameSettings.FrameRate = rate;
            GameSettings.ApplyFrameCap();
            RefreshDisplaySettings();
        }

        /// <summary>VSync switch first; the frame rate tiles are off while VSync caps the frames.</summary>
        private void RefreshDisplaySettings(){
            bool vSync = GameSettings.VSync;
            _menuVSync.EnableInClassList("switch--on", vSync);
            int rateIndex = Array.IndexOf(GameSettings.FrameRates, GameSettings.FrameRate);
            if (rateIndex < 0) rateIndex = GameSettings.FrameRates.Length - 1;
            for (int i = 0; i < _rateTiles.Count; i++) _rateTiles[i].EnableInClassList("t--on", i == rateIndex);
            _rateTileRow.SetEnabled(!vSync);
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
