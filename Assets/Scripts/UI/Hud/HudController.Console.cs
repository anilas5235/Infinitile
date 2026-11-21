using System;
using System.Collections.Generic;
using Engine.Scripts.Commands;
using Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace UI{
    /// <summary>
    ///     The command console (Console key, T; typing "/" opens it with the "/" in the line): an input line with the
    ///     usage of the command being typed and a completion list while typing, and the output above it. Tab completes
    ///     the word being typed (again: the next item), Up and Down step through the entered lines, Enter runs the line
    ///     and closes the console unless the command failed, Menu (Esc) closes it. Keys typed into the line never
    ///     trigger game actions. Closed, the newest output lines stay for a few seconds and fade out. The commands come
    ///     from <see cref="CommandRegistry.Default" />.
    /// </summary>
    public partial class HudController{
        /// <summary>Output lines shown above the input line.</summary>
        private const int ConsoleShownLines = 10;

        /// <summary>Completion items shown at once.</summary>
        private const int ConsoleShownItems = 8;

        /// <summary>Seconds a closed console keeps a new line fully shown, and when it is gone.</summary>
        private const float ConsoleFadeStart = 7f, ConsoleFadeEnd = 8f;

        private readonly List<Label> _conLines = new();
        private VisualElement _con, _conBar, _conItems, _conLog, _conSuggest, _conUsage;
        private CommandConsole _console;
        private InputAction _consoleAction;
        private bool _consoleOpen, _conFading, _slashTyped;
        private TextField _conField;
        private int _conFocusFrame = -1;
        private Keyboard _conKeyboard;

        // ── Build ────────────────────────────────────────────────────────────

        private void BindConsole(){
            _con = Root.Q("console");
            if (_con == null) return;
            _conLog = Root.Q("con-log");
            _conSuggest = Root.Q("con-suggest");
            _conUsage = Root.Q("con-usage");
            _conItems = Root.Q("con-items");
            _conBar = Root.Q("con-bar");
            _conField = Root.Q<TextField>("con-field");
            _conField.textSelection.selectAllOnFocus = false;
            _conField.textSelection.selectAllOnMouseUp = false;

            _console ??= new CommandConsole(CommandRegistry.Default, () => Time.unscaledTime){
                World = new VoxelWorldCommands()
            };
            _console.LinesChanged += OnConsoleLinesChanged;

            _conLog.Clear();
            _conLines.Clear();
            for (int i = 0; i < ConsoleShownLines; i++){
                Label line = new(){ pickingMode = PickingMode.Ignore, enableRichText = false };
                line.AddToClassList("con__line");
                _conLog.Add(line);
                _conLines.Add(line);
            }

            _conField.RegisterValueChangedCallback(OnConsoleChanged);
            _conField.RegisterCallback<KeyDownEvent>(OnConsoleKeyDown, TrickleDown.TrickleDown);
            _conField.RegisterCallback<NavigationMoveEvent>(OnConsoleNavigate, TrickleDown.TrickleDown);
            _conField.RegisterCallback<FocusOutEvent>(OnConsoleFocusOut);
            _conKeyboard = Keyboard.current;
            if (_conKeyboard != null) _conKeyboard.onTextInput += OnConsoleTextInput;

            _consoleOpen = false;
            Show(_conBar, false);
            Show(_conSuggest, false);
            RefreshConsoleLog();
        }

        private void UnbindConsole(){
            if (_con == null) return;
            if (_consoleOpen) SetConsole(false);
            _console.LinesChanged -= OnConsoleLinesChanged;
            _conField.UnregisterValueChangedCallback(OnConsoleChanged);
            _conField.UnregisterCallback<KeyDownEvent>(OnConsoleKeyDown, TrickleDown.TrickleDown);
            _conField.UnregisterCallback<NavigationMoveEvent>(OnConsoleNavigate, TrickleDown.TrickleDown);
            _conField.UnregisterCallback<FocusOutEvent>(OnConsoleFocusOut);
            if (_conKeyboard != null) _conKeyboard.onTextInput -= OnConsoleTextInput;
            _conKeyboard = null;
        }

        // ── Open, close ──────────────────────────────────────────────────────

        /// <summary>Opens the console with "/" in the line when "/" was typed, else on the Console key.</summary>
        private bool ConsoleOpenPressed(out string text){
            text = _slashTyped ? "/" : "";
            return _slashTyped || Pressed(_consoleAction);
        }

        /// <summary>A "/" typed while the console is closed opens it (any keyboard layout; HandleInput decides).</summary>
        private void OnConsoleTextInput(char character){
            if (character == '/' && !_consoleOpen) _slashTyped = true;
        }

        /// <summary>Opens the console with an empty line, or closes it (also by message, for tests).</summary>
        private void SetConsole(bool open){
            ShowConsole(open, "");
        }

        /// <summary>Opens or closes the console; while it is open the cursor is free and the player's input is off.</summary>
        private void ShowConsole(bool open, string text){
            if (_con == null || open == _consoleOpen) return;
            _consoleOpen = open;
            SetPlayerInput(!open);
            Show(_conBar, open);
            if (open){
                if (_console.Player == null){
                    PlayerManager player = FindFirstObjectByType<PlayerManager>();
                    if (player) _console.Player = new PlayerCommands(player);
                }

                _console.ResetCompletion();
                _conField.SetValueWithoutNotify(text ?? "");
                // a frame later, so the key that opened the console is not typed into the line
                _conFocusFrame = Time.frameCount + 1;
            }
            else{
                _conFocusFrame = -1;
                _conField.Blur();
                _conField.SetValueWithoutNotify("");
            }

            RefreshSuggestions();
            RefreshConsoleLog();
            UpdateLayers();
        }

        /// <summary>The focus a frame after opening, and the fading lines of the closed console.</summary>
        private void UpdateConsole(){
            if (_con == null) return;
            if (_consoleOpen && _conFocusFrame >= 0 && Time.frameCount >= _conFocusFrame){
                _conFocusFrame = -1;
                _conField.Focus();
                CaretToEnd();
            }

            if (!_consoleOpen && _conFading) RefreshConsoleLog();
        }

        /// <summary>A click beside the line takes the focus away; it comes back a frame later.</summary>
        private void OnConsoleFocusOut(FocusOutEvent _){
            if (_consoleOpen && _conFocusFrame < 0) _conFocusFrame = Time.frameCount + 1;
        }

        // ── Keys ─────────────────────────────────────────────────────────────

        /// <summary>
        ///     Tab, Enter, Up and Down belong to the console, not to the text field or the focus. Each comes as a key
        ///     event and, for Tab and Enter, again as a character event; both are stopped.
        /// </summary>
        private void OnConsoleKeyDown(KeyDownEvent evt){
            if (!_consoleOpen) return;
            switch (evt.keyCode){
                case KeyCode.Tab:
                    SetConsoleText(_console.Tab(_conField.value));
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    SubmitConsole(_conField.value);
                    break;
                case KeyCode.UpArrow:
                    SetConsoleText(_console.HistoryPrevious(_conField.value));
                    break;
                case KeyCode.DownArrow:
                    SetConsoleText(_console.HistoryNext(_conField.value));
                    break;
                default:
                    if (evt.character is not ('\t' or '\n' or '\r')) return;
                    break;
            }

            evt.StopImmediatePropagation();
        }

        /// <summary>Tab must not move the focus out of the line.</summary>
        private void OnConsoleNavigate(NavigationMoveEvent evt){
            if (_consoleOpen) evt.StopImmediatePropagation();
        }

        /// <summary>
        ///     Runs a line as Enter does: the console closes unless the command failed; then the line stays to be
        ///     fixed.
        /// </summary>
        private void SubmitConsole(string text){
            if (!_consoleOpen) return;
            if (_console.Submit(text)){
                SetConsole(false);
                return;
            }

            RefreshSuggestions();
        }

        /// <summary>The Tab key, by message (tests).</summary>
        private void ConsoleTab(){
            if (_consoleOpen) SetConsoleText(_console.Tab(_conField.value));
        }

        private void OnConsoleChanged(ChangeEvent<string> _){
            _console.ResetCompletion();
            RefreshSuggestions();
        }

        /// <summary>Puts text into the line without forgetting the Tab steps, caret at the end.</summary>
        private void SetConsoleText(string text){
            _conField.SetValueWithoutNotify(text ?? "");
            CaretToEnd();
            RefreshSuggestions();
        }

        private void CaretToEnd(){
            int end = _conField.value.Length;
            _conField.SelectRange(end, end);
            // the field may place the caret again while it takes the focus
            _conField.schedule.Execute(() => _conField.SelectRange(_conField.value.Length, _conField.value.Length));
        }

        // ── Suggestions ──────────────────────────────────────────────────────

        /// <summary>
        ///     The usage of the typed command (the parameter being typed marked) and the completion items (the one the
        ///     last Tab put in marked); hidden when there is neither.
        /// </summary>
        private void RefreshSuggestions(){
            if (_conSuggest == null) return;
            _conUsage.Clear();
            _conItems.Clear();
            if (!_consoleOpen){
                Show(_conSuggest, false);
                return;
            }

            string text = _conField.value;
            Completion active = _console.ActiveCompletion(text);
            // a single item was put in with a space after it: show what comes next
            if (active != null && active.Items.Count <= 1) active = null;
            Completion completion = active ?? _console.Suggest(text);
            int on = active != null ? _console.CompletionIndex : -1;

            CommandLine line = CommandLine.Parse(text);
            bool usage = _console.Registry.TryGet(line.CommandName, out ConsoleCommand command);
            if (usage){
                // the parameter being typed: the completion's, or, on the name, none
                int marked = completion.Command == command ? completion.ParameterIndex : -1;
                AddUsageWord(command.Name, "con__word--name");
                for (int i = 0; i < command.Parameters.Count; i++)
                    AddUsageWord(command.Parameters[i].Signature, i == marked ? "con__word--on" : null);
            }

            IReadOnlyList<string> items = completion.Items;
            string typed = text[completion.ReplaceStart..];
            bool nothingToAdd = items.Count == 1 && on < 0 &&
                                string.Equals(items[0], typed, StringComparison.OrdinalIgnoreCase);
            int shown = 0;
            if (!nothingToAdd && items.Count > 0){
                int first = on >= ConsoleShownItems ? on - ConsoleShownItems + 1 : 0;
                for (int i = first; i < items.Count && shown < ConsoleShownItems; i++, shown++){
                    Label item = new(items[i]){ pickingMode = PickingMode.Ignore, enableRichText = false };
                    item.AddToClassList("con__item");
                    item.EnableInClassList("con__item--on", i == on);
                    _conItems.Add(item);
                }

                int more = items.Count - shown;
                if (more > 0){
                    Label rest = new($"{more} more"){ pickingMode = PickingMode.Ignore };
                    rest.AddToClassList("con__item");
                    rest.AddToClassList("con__more");
                    _conItems.Add(rest);
                }
            }

            Show(_conUsage, usage);
            Show(_conItems, shown > 0);
            Show(_conSuggest, usage || shown > 0);
        }

        private void AddUsageWord(string word, string modifier){
            Label label = new(word){ pickingMode = PickingMode.Ignore, enableRichText = false };
            label.AddToClassList("con__word");
            if (modifier != null) label.AddToClassList(modifier);
            _conUsage.Add(label);
        }

        // ── Output ───────────────────────────────────────────────────────────

        private void OnConsoleLinesChanged(){
            RefreshConsoleLog();
        }

        /// <summary>
        ///     The newest lines, oldest on top. Open: all of them. Closed: the lines younger than
        ///     <see cref="ConsoleFadeEnd" /> seconds, fading out after <see cref="ConsoleFadeStart" />.
        /// </summary>
        private void RefreshConsoleLog(){
            if (_conLog == null) return;
            IReadOnlyList<ConsoleLine> lines = _console.Lines;
            int first = Mathf.Max(0, lines.Count - ConsoleShownLines);
            float now = Time.unscaledTime;
            _conFading = false;
            for (int i = 0; i < _conLines.Count; i++){
                Label label = _conLines[i];
                int index = first + i;
                if (index >= lines.Count){
                    Show(label, false);
                    continue;
                }

                ConsoleLine line = lines[index];
                float opacity = 1f;
                if (!_consoleOpen){
                    float age = now - line.Time;
                    opacity = 1f - Mathf.Clamp01((age - ConsoleFadeStart) / (ConsoleFadeEnd - ConsoleFadeStart));
                    if (opacity > 0f) _conFading = true;
                }

                Show(label, opacity > 0f);
                if (opacity <= 0f) continue;
                if (label.text != line.Text) label.text = line.Text;
                label.style.opacity = opacity;
                label.EnableInClassList("con__line--input", line.Kind == ConsoleLineKind.Input);
                label.EnableInClassList("con__line--error", line.Kind == ConsoleLineKind.Error);
            }
        }
    }
}
