using System;
using System.Collections.Generic;
using UnityEngine;

namespace Engine.Scripts.Commands{
    /// <summary>The kind of a console line, for its colour.</summary>
    public enum ConsoleLineKind : byte{
        /// <summary>A line the player entered, shown back.</summary>
        Input,

        /// <summary>Output of a command.</summary>
        Info,

        /// <summary>Bad input or a failed command.</summary>
        Error
    }

    /// <summary>One line of console output and when it was written (the console's clock).</summary>
    public readonly struct ConsoleLine{
        public readonly ConsoleLineKind Kind;
        public readonly string Text;
        public readonly float Time;

        public ConsoleLine(ConsoleLineKind kind, string text, float time){
            Kind = kind;
            Text = text;
            Time = time;
        }
    }

    /// <summary>
    ///     The command console without its UI: runs entered lines through a <see cref="CommandRegistry" />, keeps the
    ///     output and the history of entered lines, and completes the line with Tab (repeated Tabs step through the
    ///     items).
    /// </summary>
    public sealed class CommandConsole{
        /// <summary>Lines of output kept; older ones are dropped.</summary>
        public const int MaxLines = 100;

        /// <summary>Entered lines kept for the history.</summary>
        public const int MaxHistory = 50;

        private readonly Func<float> _clock;
        private readonly List<string> _history = new();
        private readonly List<ConsoleLine> _lines = new();

        private Completion _cycle;
        private int _cycleIndex = -1;
        private string _cycleText;
        private string _draft = "";
        private int _historyIndex;

        /// <param name="registry">The commands.</param>
        /// <param name="clock">Time stamp of new lines, in seconds; 0 when null.</param>
        public CommandConsole(CommandRegistry registry, Func<float> clock = null){
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _clock = clock;
        }

        public CommandRegistry Registry{ get; }

        /// <summary>The world commands act on; may be null.</summary>
        public ICommandWorld World{ get; set; }

        /// <summary>The player commands act on; may be null.</summary>
        public ICommandPlayer Player{ get; set; }

        /// <summary>The output, oldest first.</summary>
        public IReadOnlyList<ConsoleLine> Lines => _lines;

        /// <summary>The entered lines, oldest first.</summary>
        public IReadOnlyList<string> History => _history;

        /// <summary>The item of the last Tab completion, -1 when the line was changed since.</summary>
        public int CompletionIndex => _cycleIndex;

        /// <summary>Raised when lines are added or cleared.</summary>
        public event Action LinesChanged;

        public CommandContext Context => new(Registry, this, World, Player);

        // ── Output ───────────────────────────────────────────────────────────

        public void Print(string text){
            Add(ConsoleLineKind.Info, text);
        }

        public void PrintError(string text){
            Add(ConsoleLineKind.Error, text);
        }

        public void Clear(){
            _lines.Clear();
            LinesChanged?.Invoke();
        }

        private void Add(ConsoleLineKind kind, string text){
            if (text == null) return;
            foreach (string part in text.Split('\n')){
                _lines.Add(new ConsoleLine(kind, part, _clock?.Invoke() ?? 0f));
                if (_lines.Count > MaxLines) _lines.RemoveAt(0);
            }

            LinesChanged?.Invoke();
        }

        // ── Running ──────────────────────────────────────────────────────────

        /// <summary>
        ///     Runs an entered line: it goes to the history and is shown back, then the command runs and its result is
        ///     printed. True when the command ran and succeeded; an empty line does nothing and returns true.
        /// </summary>
        public bool Submit(string text){
            ResetCompletion();
            string trimmed = text?.Trim() ?? "";
            _historyIndex = _history.Count;
            if (trimmed.Length == 0 || trimmed == "/") return true;

            if (_history.Count == 0 || _history[^1] != trimmed){
                _history.Add(trimmed);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }

            _historyIndex = _history.Count;
            _draft = "";
            Add(ConsoleLineKind.Input, trimmed);
            return Execute(trimmed);
        }

        /// <summary>Runs a line without the history or the echo; errors are printed.</summary>
        public bool Execute(string text){
            CommandLine line = CommandLine.Parse(text);
            IReadOnlyList<CommandToken> tokens = line.Tokens;
            if (tokens.Count == 0) return true;

            if (!Registry.TryGet(line.CommandName, out ConsoleCommand command)){
                PrintError($"Unknown command '{line.CommandName}'. Type help for the list of commands.");
                return false;
            }

            CommandContext context = Context;
            IReadOnlyList<CommandParameter> parameters = command.Parameters;
            int given = tokens.Count - 1;
            if (given < command.RequiredCount){
                PrintError($"Missing {parameters[given].Signature}. Usage: {command.Usage}");
                return false;
            }

            if (given > parameters.Count){
                PrintError($"Too many arguments. Usage: {command.Usage}");
                return false;
            }

            object[] values = new object[given];
            for (int i = 0; i < given; i++){
                if (parameters[i].TryParse(tokens[i + 1].Text, context, out values[i], out string error)) continue;
                PrintError(error);
                return false;
            }

            CommandResult result;
            try{
                result = command.Execute(context, new CommandArguments(values));
            }
            catch (Exception e){
                PrintError($"{command.Name} failed: {e.Message}");
                Debug.LogException(e);
                return false;
            }

            if (result.Success) Print(result.Message);
            else PrintError(result.Message ?? $"{command.Name} failed.");
            return result.Success;
        }

        // ── Completion ───────────────────────────────────────────────────────

        /// <summary>What completion offers for the line, for the list shown while typing.</summary>
        public Completion Suggest(string text){
            return CommandCompleter.Complete(text, Context);
        }

        /// <summary>
        ///     The line after Tab: the word being typed replaced by the first item. Tab again, on the line it gave,
        ///     replaces it by the next item, round. A single item gets a space after it, so the next word can start.
        ///     The line is unchanged when nothing fits.
        /// </summary>
        public string Tab(string text){
            text ??= "";
            if (_cycle != null && text == _cycleText && _cycle.Items.Count > 1){
                _cycleIndex = (_cycleIndex + 1) % _cycle.Items.Count;
            }
            else{
                Completion completion = Suggest(text);
                if (completion.Items.Count == 0){
                    ResetCompletion();
                    return text;
                }

                _cycle = completion;
                _cycleIndex = 0;
            }

            _cycleText = _cycle.Apply(_cycleIndex, _cycle.Items.Count == 1);
            return _cycleText;
        }

        /// <summary>The completion the last Tab stepped through, while the line is still what it gave.</summary>
        public Completion ActiveCompletion(string text){
            return _cycle != null && text == _cycleText ? _cycle : null;
        }

        /// <summary>Forgets the Tab steps: the next Tab completes the line anew.</summary>
        public void ResetCompletion(){
            _cycle = null;
            _cycleText = null;
            _cycleIndex = -1;
        }

        // ── History ──────────────────────────────────────────────────────────

        /// <summary>
        ///     The entered line before the one shown (Up); the first step keeps <paramref name="current" /> as the
        ///     draft that <see cref="HistoryNext" /> comes back to. Stays on the oldest line.
        /// </summary>
        public string HistoryPrevious(string current){
            if (_history.Count == 0) return current;
            if (_historyIndex >= _history.Count){
                _historyIndex = _history.Count;
                _draft = current ?? "";
            }

            if (_historyIndex > 0) _historyIndex--;
            ResetCompletion();
            return _history[_historyIndex];
        }

        /// <summary>The entered line after the one shown (Down); past the newest, the draft.</summary>
        public string HistoryNext(string current){
            if (_historyIndex >= _history.Count) return current;
            _historyIndex++;
            ResetCompletion();
            return _historyIndex >= _history.Count ? _draft : _history[_historyIndex];
        }
    }
}
