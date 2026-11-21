using System.Collections.Generic;

namespace Engine.Scripts.Commands{
    /// <summary>One word of a command line and where it starts in the line.</summary>
    public readonly struct CommandToken{
        public readonly string Text;
        public readonly int Start;

        public CommandToken(string text, int start){
            Text = text;
            Start = start;
        }

        /// <summary>The index just after the token.</summary>
        public int End => Start + Text.Length;
    }

    /// <summary>
    ///     A typed command line split into words at whitespace. A leading "/" (as in "/teleport") is optional and not
    ///     part of the first word. Words keep their place in the line, so completion can replace the last one.
    /// </summary>
    public sealed class CommandLine{
        private readonly List<CommandToken> _tokens = new();

        private CommandLine(string text){
            Text = text ?? "";
            int i = 0;
            while (i < Text.Length && char.IsWhiteSpace(Text[i])) i++;
            if (i < Text.Length && Text[i] == '/') i++;
            BodyStart = i;

            while (i < Text.Length){
                if (char.IsWhiteSpace(Text[i])){
                    i++;
                    continue;
                }

                int start = i;
                while (i < Text.Length && !char.IsWhiteSpace(Text[i])) i++;
                _tokens.Add(new CommandToken(Text[start..i], start));
            }

            EndsWithSpace = Text.Length > BodyStart && char.IsWhiteSpace(Text[^1]);
        }

        /// <summary>The line as typed.</summary>
        public string Text{ get; }

        /// <summary>Where the command word starts or would start: after leading spaces and an optional "/".</summary>
        public int BodyStart{ get; }

        /// <summary>The words of the line; the first is the command name.</summary>
        public IReadOnlyList<CommandToken> Tokens => _tokens;

        /// <summary>The line ends in whitespace: the next word has not been started.</summary>
        public bool EndsWithSpace{ get; }

        /// <summary>The command word, "" when the line has none.</summary>
        public string CommandName => _tokens.Count > 0 ? _tokens[0].Text : "";

        public static CommandLine Parse(string text){
            return new CommandLine(text);
        }
    }
}
