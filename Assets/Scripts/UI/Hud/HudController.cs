using System;
using System.Collections.Generic;
using Engine.Scripts.Render;
using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Player;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using static System.FormattableString;
using Cursor = UnityEngine.Cursor;

namespace UI{
    /// <summary>
    ///     The in-game HUD (UI Toolkit, Hud.uxml): crosshair, a hotbar of voxels to place, and three debug panels
    ///     toggled with F7: the world under the player (position, chunk, biome, climate, generation methods), the
    ///     engine's performance (frame time, graphics memory, chunk queues, culling pages per layer) and the world generation controls
    ///     of <see cref="WorldGenDebugKeys" />. Holding Left Alt frees the cursor to click the panel buttons. E opens
    ///     a list of every voxel, searchable by name or id, that puts the clicked voxel into the hotbar.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class HudController : MonoBehaviour{
        [SerializeField] private Texture2D crosshair;

        [Tooltip("Voxels of hotbar slots 1–9 by full name (package:name). Middle click replaces the selected slot.")]
        [SerializeField] private string[] hotbarVoxels = {
            "std:Grass", "std:Dirt", "std:Stone", "std:Sand", "std:LogOak", "std:Planks", "std:Leaves", "std:Glass",
            "std:Water"
        };

        [SerializeField] private Key debugPanelsKey = Key.F7;
        [SerializeField] private Key cursorKey = Key.LeftAlt;
        [SerializeField] private Key voxelSearchKey = Key.E;
        [SerializeField] private bool showDebugPanels = true;

        [Tooltip("Seconds between text refreshes of the debug panels.")]
        [SerializeField] private float refreshInterval = 0.1f;

        [SerializeField] private VoxelEditor voxelEditor;
        [SerializeField] private WorldGenDebugKeys worldGenKeys;

        private readonly List<CullingLayerStats> _cullingStats = new();
        private readonly List<Label> _cullingRows = new();
        private readonly List<SearchEntry> _searchEntries = new();
        private readonly List<VisualElement> _slots = new();
        private Button[] _bookmarkGo;
        private Label _biome, _chunk, _chunks, _climate, _configuration, _fps, _generation, _height, _hint;
        private Label _message, _position, _queues, _vram, _voxelName;
        private VisualElement _crosshair, _culling, _debugLeft, _debugRight, _search;
        private TextField _searchField;
        private Label _searchName;
        private ScrollView _searchResults;

        private bool _cursorFree;
        private int _focusSearchFrame = -1;
        private float _frameTimeSum;
        private int _frames;
        private ProfilerRecorder _gfxMemory;
        private LookController _look;
        private PlayerControllerManager _movement;
        private float _nextRefresh;
        private PlayerInput _playerInput;
        private int _searchBaseCount;
        private bool _searchOpen;
        private int _selected;
        private ushort[] _slotIds;

        /// <summary>The HUD's root element (the "hud" element of Hud.uxml).</summary>
        public VisualElement Root{ get; private set; }

        private void OnEnable(){
            Root = GetComponent<UIDocument>().rootVisualElement.Q("hud");
            if (Root == null) return;
            if (!voxelEditor) voxelEditor = FindFirstObjectByType<VoxelEditor>();
            if (!worldGenKeys) worldGenKeys = FindFirstObjectByType<WorldGenDebugKeys>();
            _look = FindFirstObjectByType<LookController>();
            _movement = FindFirstObjectByType<PlayerControllerManager>();

            _crosshair = Root.Q("crosshair");
            _crosshair.style.backgroundImage = crosshair;
            _message = Root.Q<Label>("message");
            _hint = Root.Q<Label>("hint");
            _debugLeft = Root.Q("debug-left");
            _debugRight = Root.Q("debug-right");
            _position = Root.Q<Label>("position");
            _chunk = Root.Q<Label>("chunk");
            _biome = Root.Q<Label>("biome");
            _height = Root.Q<Label>("height");
            _climate = Root.Q<Label>("climate");
            _generation = Root.Q<Label>("generation");
            _fps = Root.Q<Label>("fps");
            _vram = Root.Q<Label>("vram");
            _chunks = Root.Q<Label>("chunks");
            _queues = Root.Q<Label>("queues");
            _culling = Root.Q("culling");
            _configuration = Root.Q<Label>("configuration");
            _voxelName = Root.Q<Label>("voxel-name");

            // Unity's own graphics allocations (textures, meshes, buffers, render targets); in the Editor this
            // includes the Editor's windows.
            _gfxMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
            BindGenControls();
            BuildHotbar();
            BindVoxelSearch();
            if (voxelEditor) voxelEditor.OnVoxelIdChanged += OnVoxelIdChanged;
            SetDebugPanels(showDebugPanels);
        }

        private void OnDisable(){
            if (voxelEditor) voxelEditor.OnVoxelIdChanged -= OnVoxelIdChanged;
            if (_searchOpen) SetVoxelSearch(false);
            _searchField?.UnregisterValueChangedCallback(OnSearchChanged);
            _searchField?.UnregisterCallback<FocusOutEvent>(OnSearchFocusOut);
            if (_cursorFree) SetCursorFree(false);
            _gfxMemory.Dispose();
            if (VoxelWorldRenderer.Instance) VoxelWorldRenderer.Instance.CollectCullingStats = false;
        }

        private void Update(){
            if (Root == null) return;
            _frameTimeSum += Time.unscaledDeltaTime;
            _frames++;

            HandleInput();
            if (_searchOpen && _focusSearchFrame >= 0 && Time.frameCount >= _focusSearchFrame){
                _focusSearchFrame = -1;
                _searchField.Focus();
            }

            if (!HotbarResolved()) ResolveHotbar(); // the registry fills in the DataImporter's Awake

            string message = worldGenKeys ? worldGenKeys.Message : null;
            _message.text = message;
            _message.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + refreshInterval;
            if (showDebugPanels){
                RefreshWorld();
                RefreshPerformance();
                RefreshGenControls();
            }

            _frameTimeSum = 0f;
            _frames = 0;
        }

        private void HandleInput(){
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard[debugPanelsKey].wasPressedThisFrame) SetDebugPanels(!showDebugPanels);
            if (_searchOpen){
                // A search key that types (a letter, say) is part of the query; any other closes the list again.
                if (keyboard.escapeKey.wasPressedThisFrame ||
                    (keyboard[voxelSearchKey].wasPressedThisFrame && !TypesText(voxelSearchKey)))
                    SetVoxelSearch(false);
                else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                    PickFirstMatch();
            }
            else if (keyboard[voxelSearchKey].wasPressedThisFrame){
                SetVoxelSearch(true);
            }

            bool cursorFree = _searchOpen || keyboard[cursorKey].isPressed;
            if (cursorFree != _cursorFree) SetCursorFree(cursorFree);

            if (_slotIds.Length == 0 || _cursorFree) return;
            for (int i = 0; i < _slotIds.Length; i++)
                if (keyboard[Key.Digit1 + i].wasPressedThisFrame)
                    SelectSlot(i);

            Mouse mouse = Mouse.current;
            float scroll = mouse != null ? mouse.scroll.ReadValue().y : 0f;
            if (scroll != 0f) SelectSlot((_selected + (scroll < 0f ? 1 : -1) + _slotIds.Length) % _slotIds.Length);
        }

        private void SetDebugPanels(bool show){
            showDebugPanels = show;
            DisplayStyle display = show ? DisplayStyle.Flex : DisplayStyle.None;
            _debugLeft.style.display = display;
            _debugRight.style.display = display;
            if (VoxelWorldRenderer.Instance) VoxelWorldRenderer.Instance.CollectCullingStats = show;
            string modes = _movement ? $"    {_movement.ModeKey}: walk / fly / spectator" : "";
            _hint.text = show
                ? $"{debugPanelsKey}: hide panels    hold {cursorKey}: cursor    1–9 / wheel: voxel    middle click: pick" +
                  $"    {voxelSearchKey}: all voxels{modes}"
                : $"{debugPanelsKey}: debug panels    {voxelSearchKey}: all voxels{modes}";
            _nextRefresh = 0f;
        }

        /// <summary>Unlocks the cursor and stops looking and editing while the cursor key is held or the voxel list is open.</summary>
        private void SetCursorFree(bool free){
            _cursorFree = free;
            Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = free;
            if (_look) _look.enabled = !free;
            if (!voxelEditor) return;
            voxelEditor.enabled = !free;
            voxelEditor.destruct = voxelEditor.place = false;
        }

        // ── World panel ──────────────────────────────────────────────────────

        private void RefreshWorld(){
            Camera cam = Camera.main;
            VoxelWorld world = VoxelWorld.Instance;
            if (!cam || !world) return;

            Vector3 pos = cam.transform.position;
            int3 block = new((int)math.floor(pos.x), (int)math.floor(pos.y), (int)math.floor(pos.z));
            _position.text = Invariant($"Position {pos.x:F1} {pos.y:F1} {pos.z:F1}") +
                             $"   facing {Facing(cam.transform.forward)}" +
                             (_movement ? $"   {_movement.Mode}" : "");
            int2 chunk = VoxelConstants.WorldToChunkPos(block);
            _chunk.text = $"Chunk {chunk.x} {chunk.y}   block in chunk {block.x - chunk.x * VoxelConstants.ChunkWidth} " +
                          $"{block.z - chunk.y * VoxelConstants.ChunkWidth}";

            if (world.TrySampleColumn(block.x, block.z, out ColumnSample sample)){
                string surface = world.BiomeName(sample.SurfaceSource) ?? "?";
                string hard = world.BiomeName(sample.HardBiome) ?? "?";
                _biome.text = surface == hard ? $"Biome {surface}" : $"Biome {surface} (hard {hard})";
                WorldGenSettings worldGen = world.Settings.WorldGen;
                _height.text = $"Ground {sample.ColumnHeight}   water {(worldGen ? worldGen.WaterLevel : 0)}   " +
                               $"above ground {block.y - sample.ColumnHeight}";
                WorldFields f = sample.Fields;
                _climate.text = Invariant($"T {f.T:F2}   F {f.F:F2}   C {f.C:F2}   E {f.E:F2}   W {f.W:F2}");
            }
            else{
                _biome.text = "Biome -";
                _height.text = _climate.text = "";
            }

            _generation.text = world.TryGetGenerationMethods(out GenArchitecture architecture,
                out BlendMethod material, out BlendMethod height)
                ? $"{architecture}   material {material}   height {height}"
                : "";
        }

        private static string Facing(Vector3 forward){
            if (math.abs(forward.x) > math.abs(forward.z)) return forward.x > 0f ? "+x" : "-x";
            return forward.z > 0f ? "+z" : "-z";
        }

        // ── Performance panel ────────────────────────────────────────────────

        private void RefreshPerformance(){
            float ms = _frames > 0 ? _frameTimeSum / _frames * 1000f : 0f;
            _fps.text = ms > 0f ? Invariant($"{1000f / ms:F0} FPS   {ms:F1} ms") : "- FPS";
            string usedMb = _gfxMemory.Valid ? $"{_gfxMemory.LastValue / (1024 * 1024)}" : "-";
            _vram.text = $"VRAM {usedMb} / {SystemInfo.graphicsMemorySize} MB";

            VoxelWorld world = VoxelWorld.Instance;
            if (world){
                WorldStats stats = world.Stats;
                _chunks.text = $"Chunks loaded {stats.LoadedChunks}";
                _queues.text = $"Queues   data {stats.DataQueue}   light {stats.LightQueue}   " +
                               $"mesh {stats.MeshQueue}   bake {stats.BakeQueue}";
            }

            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            if (renderer){
                renderer.CollectCullingStats = true;
                renderer.GetCullingStats(_cullingStats);
            }
            else{
                _cullingStats.Clear();
            }

            while (_cullingRows.Count < _cullingStats.Count){
                Label row = new();
                row.AddToClassList("row");
                _culling.Add(row);
                _cullingRows.Add(row);
            }

            for (int i = 0; i < _cullingRows.Count; i++){
                bool used = i < _cullingStats.Count;
                _cullingRows[i].style.display = used ? DisplayStyle.Flex : DisplayStyle.None;
                if (!used) continue;
                CullingLayerStats s = _cullingStats[i];
                _cullingRows[i].text = $"{s.Layer}   {s.VisiblePages} / {s.ShadowPages} / {s.AllocatedPages}" +
                                       $"   ({s.Buffers} buffer{(s.Buffers == 1 ? "" : "s")})";
            }
        }

        // ── World generation panel ───────────────────────────────────────────

        private void BindGenControls(){
            Root.Q("gen-panel").style.display = worldGenKeys ? DisplayStyle.Flex : DisplayStyle.None;
            _bookmarkGo = Array.Empty<Button>();
            if (!worldGenKeys) return;

            Root.Q<Button>("previous-config").clicked += () => worldGenKeys.StepConfiguration(-1);
            Root.Q<Button>("next-config").clicked += () => worldGenKeys.StepConfiguration(1);
            Root.Q<Button>("architecture").clicked += worldGenKeys.SwitchArchitecture;

            VisualElement bookmarks = Root.Q("bookmarks");
            bookmarks.Clear();
            _bookmarkGo = new Button[worldGenKeys.BookmarkCount];
            for (int i = 0; i < _bookmarkGo.Length; i++){
                int index = i;
                VisualElement entry = new();
                entry.AddToClassList("bookmark");
                Button go = new(() => worldGenKeys.RestoreBookmark(index)){ text = $"{index + 1}", tooltip = "Go" };
                Button store = new(() => worldGenKeys.StoreBookmark(index)){ text = "Store" };
                go.AddToClassList("hud-button");
                store.AddToClassList("hud-button");
                entry.Add(go);
                entry.Add(store);
                bookmarks.Add(entry);
                _bookmarkGo[i] = go;
            }
        }

        private void RefreshGenControls(){
            if (!worldGenKeys) return;
            _configuration.text = $"Config {worldGenKeys.CurrentConfiguration}";
            for (int i = 0; i < _bookmarkGo.Length; i++)
                _bookmarkGo[i].EnableInClassList("hud-button--empty", !worldGenKeys.HasBookmark(i));
        }

        // ── Hotbar ───────────────────────────────────────────────────────────

        private void BuildHotbar(){
            VisualElement hotbar = Root.Q("hotbar");
            hotbar.Clear();
            _slots.Clear();
            _slotIds = new ushort[math.min(hotbarVoxels.Length, 9)];
            for (int i = 0; i < _slotIds.Length; i++){
                VisualElement slot = CreateSlot($"{i + 1}");
                slot.pickingMode = PickingMode.Ignore;
                hotbar.Add(slot);
                _slots.Add(slot);
            }

            ResolveHotbar();
        }

        /// <summary>A slot of the hotbar or the voxel list: the voxel's icon (element "icon") with a label in the corner.</summary>
        private static VisualElement CreateSlot(string keyText){
            VisualElement slot = new();
            slot.AddToClassList("slot");
            VisualElement icon = new(){ name = "icon", pickingMode = PickingMode.Ignore };
            icon.AddToClassList("slot__icon");
            Label key = new(keyText){ pickingMode = PickingMode.Ignore };
            key.AddToClassList("slot__key");
            slot.Add(icon);
            slot.Add(key);
            return slot;
        }

        private static Texture2D IconOf(Voxel voxel){
            return voxel.GetDisplayTexture(QuadDrawCondition.Forward);
        }

        private bool HotbarResolved(){
            for (int i = 0; i < _slotIds.Length; i++)
                if (_slotIds[i] == 0 && !string.IsNullOrEmpty(hotbarVoxels[i]))
                    return false;
            return true;
        }

        /// <summary>Looks the slot names up in the voxel registry; slots of unknown names stay empty.</summary>
        private void ResolveHotbar(){
            VoxelRegistry registry = Registry();
            if (registry == null) return;
            bool any = false;
            for (int i = 0; i < _slotIds.Length; i++){
                if (_slotIds[i] != 0 || string.IsNullOrEmpty(hotbarVoxels[i])) continue;
                if (!registry.TryGetId(new FixedString32Bytes(hotbarVoxels[i]), out ushort id)) continue;
                SetSlot(i, id);
                any = true;
            }

            if (!any) return;
            int current = voxelEditor ? Array.IndexOf(_slotIds, voxelEditor.voxelId) : -1;
            SelectSlot(current >= 0 ? current : _selected);
        }

        private void SetSlot(int index, ushort id){
            _slotIds[index] = id;
            VoxelRegistry registry = Registry();
            Texture2D texture = registry != null && registry.TryGet(id, out Voxel voxel) ? IconOf(voxel) : null;
            _slots[index].Q("icon").style.backgroundImage = texture;
        }

        private void SelectSlot(int index){
            _selected = index;
            for (int i = 0; i < _slots.Count; i++) _slots[i].EnableInClassList("slot--selected", i == index);
            ushort id = _slotIds[index];
            if (voxelEditor && id != 0) voxelEditor.SelectVoxel(id);
            ShowVoxelName(id);
        }

        /// <summary>A pick (middle click) selects the slot holding the voxel, or puts it into the selected slot.</summary>
        private void OnVoxelIdChanged(ushort id){
            if (_slotIds.Length == 0 || _slotIds[_selected] == id) return;
            int existing = Array.IndexOf(_slotIds, id);
            if (existing >= 0){
                SelectSlot(existing);
                return;
            }

            SetSlot(_selected, id);
            ShowVoxelName(id);
        }

        private void ShowVoxelName(ushort id){
            VoxelRegistry registry = Registry();
            _voxelName.text = registry != null && registry.TryGet(id, out Voxel voxel) ? voxel.name : "";
            _voxelName.style.display = string.IsNullOrEmpty(_voxelName.text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static VoxelRegistry Registry(){
            return DataImporter.Instance ? DataImporter.Instance.VoxelRegistry : null;
        }

        // ── Voxel search ─────────────────────────────────────────────────────

        private void BindVoxelSearch(){
            _search = Root.Q("voxel-search");
            _searchField = Root.Q<TextField>("voxel-search-field");
            _searchResults = Root.Q<ScrollView>("voxel-search-results");
            _searchName = Root.Q<Label>("voxel-search-name");
            _searchField.textEdition.placeholder = "Name or id";
            _searchField.RegisterValueChangedCallback(OnSearchChanged);
            _searchField.RegisterCallback<FocusOutEvent>(OnSearchFocusOut);
            _searchEntries.Clear();
            _searchResults.Clear();
            _searchBaseCount = 0;
            _search.style.display = DisplayStyle.None;
        }

        private void OnSearchChanged(ChangeEvent<string> _){
            FilterVoxelSearch();
        }

        /// <summary>
        ///     A click on the scrollbar or the panel takes the focus from the field; it comes back a frame later, so
        ///     typing goes on filtering.
        /// </summary>
        private void OnSearchFocusOut(FocusOutEvent _){
            if (_searchOpen && _focusSearchFrame < 0) _focusSearchFrame = Time.frameCount + 1;
        }

        /// <summary>Opens or closes the voxel list; while it is open the cursor is free and the player's input is off.</summary>
        private void SetVoxelSearch(bool open){
            _searchOpen = open;
            _search.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            _crosshair.style.display = open ? DisplayStyle.None : DisplayStyle.Flex;
            // Looked up on every open: the player may spawn after the HUD, or be replaced.
            if (open) _playerInput = FindFirstObjectByType<PlayerInput>();
            if (_playerInput){
                if (open) _playerInput.DeactivateInput();
                // In teardown the PlayerInput may already be disabled; it activates itself when enabled again.
                else if (_playerInput.isActiveAndEnabled) _playerInput.ActivateInput();
            }

            if (!open){
                _focusSearchFrame = -1;
                _searchField.Blur();
                return;
            }

            BuildVoxelSearch();
            _searchField.SetValueWithoutNotify("");
            FilterVoxelSearch();
            _searchResults.scrollOffset = Vector2.zero;
            // Focus a frame later, so the key that opened the list is not typed into the field.
            _focusSearchFrame = Time.frameCount + 1;
        }

        /// <summary>
        ///     One slot per base voxel in id order; turned variants are placed from their base. Rebuilt when the
        ///     registry's voxel count changes (it fills in the DataImporter's Awake).
        /// </summary>
        private void BuildVoxelSearch(){
            VoxelRegistry registry = Registry();
            int baseCount = registry?.BaseCount ?? 0;
            if (baseCount == _searchBaseCount) return;
            _searchBaseCount = baseCount;
            _searchEntries.Clear();
            _searchResults.Clear();
            for (ushort id = 1; id < baseCount; id++){
                if (!registry.TryGet(id, out Voxel voxel)) continue;
                string fullName = registry.TryGetName(id, out FixedString32Bytes name) ? name.ToString() : voxel.name;
                ushort voxelId = id;
                VisualElement slot = CreateSlot($"{id}");
                slot.Q("icon").style.backgroundImage = IconOf(voxel);
                slot.RegisterCallback<ClickEvent>(_ => PickFromSearch(voxelId));
                slot.RegisterCallback<PointerEnterEvent>(_ => _searchName.text = $"{fullName}   id {voxelId}");
                slot.RegisterCallback<PointerLeaveEvent>(_ => _searchName.text = "");
                _searchResults.Add(slot);
                _searchEntries.Add(new SearchEntry{
                    Id = voxelId, IdText = $"{id}", Name = fullName.ToLowerInvariant(), Slot = slot
                });
            }
        }

        /// <summary>Shows the voxels whose full name contains the query or whose id starts with it.</summary>
        private void FilterVoxelSearch(){
            string query = _searchField.value.Trim().ToLowerInvariant();
            int shown = 0;
            foreach (SearchEntry entry in _searchEntries){
                entry.Visible = query.Length == 0 || entry.Name.Contains(query) ||
                                entry.IdText.StartsWith(query, StringComparison.Ordinal);
                entry.Slot.style.display = entry.Visible ? DisplayStyle.Flex : DisplayStyle.None;
                entry.Slot.EnableInClassList("slot--selected", voxelEditor && entry.Id == voxelEditor.voxelId);
                if (entry.Visible) shown++;
            }

            _searchName.text = _searchEntries.Count == 0 ? "The voxels are not loaded yet."
                : shown == 0 ? "No voxel matches."
                : "";
        }

        private void PickFirstMatch(){
            foreach (SearchEntry entry in _searchEntries)
                if (entry.Visible){
                    PickFromSearch(entry.Id);
                    return;
                }
        }

        /// <summary>Selects the slot holding the voxel, or puts it into the selected slot, and closes the list.</summary>
        private void PickFromSearch(ushort id){
            if (_slotIds.Length == 0){
                if (voxelEditor) voxelEditor.SelectVoxel(id);
            }
            else{
                int existing = Array.IndexOf(_slotIds, id);
                if (existing < 0) SetSlot(_selected, id);
                SelectSlot(existing >= 0 ? existing : _selected);
            }

            SetVoxelSearch(false);
        }

        /// <summary>The key types a character into a focused text field.</summary>
        private static bool TypesText(Key key){
            return key == Key.Space || (key >= Key.Backquote && key <= Key.Digit0) ||
                   (key >= Key.NumpadDivide && key <= Key.Numpad9);
        }

        private class SearchEntry{
            public ushort Id;
            public string IdText, Name;
            public VisualElement Slot;
            public bool Visible;
        }
    }
}
