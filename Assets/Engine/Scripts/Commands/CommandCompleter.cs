using System.Collections.Generic;

namespace Engine.Scripts.Commands{
    /// <summary>
    ///     What completion offers for the end of a line: the word being typed (from <see cref="ReplaceStart" /> to the
    ///     end of <see cref="Text" />), the items that can replace it, and the command and parameter it belongs to.
    /// </summary>
    public sealed class Completion{
        public static readonly Completion None = new("", 0, new List<string>(), null, -1);

        public Completion(string text, int replaceStart, List<string> items, ConsoleCommand command, int parameterIndex){
            Text = text ?? "";
            ReplaceStart = replaceStart;
            Items = items;
            Command = command;
            ParameterIndex = parameterIndex;
        }

        /// <summary>The line the completion was made for.</summary>
        public string Text{ get; }

        /// <summary>Where the word being completed starts; it runs to the end of the line.</summary>
        public int ReplaceStart{ get; }

        /// <summary>The words that fit, best first; empty when nothing fits.</summary>
        public IReadOnlyList<string> Items{ get; }

        /// <summary>The command of the line, null while its name is typed or when it is unknown.</summary>
        public ConsoleCommand Command{ get; }

        /// <summary>The parameter being typed; -1 for the command name or past the last parameter.</summary>
        public int ParameterIndex{ get; }

        /// <summary>The line with the word replaced by item <paramref name="index" />, and a space after it if asked.</summary>
        public string Apply(int index, bool space){
            return Text[..ReplaceStart] + Items[index] + (space ? " " : "");
        }
    }

    /// <summary>Completion of a command line: command names first, then each parameter's own suggestions.</summary>
    public static class CommandCompleter{
        public static Completion Complete(string text, CommandContext context){
            text ??= "";
            CommandRegistry registry = context?.Registry;
            if (registry == null) return Completion.None;
            CommandLine line = CommandLine.Parse(text);
            IReadOnlyList<CommandToken> tokens = line.Tokens;

            // the word being typed: a new one after a space (or in an empty line), else the last
            bool newWord = tokens.Count == 0 || line.EndsWithSpace;
            int index = newWord ? tokens.Count : tokens.Count - 1;
            int start = newWord ? text.Length : tokens[index].Start;
            string prefix = newWord ? "" : tokens[index].Text;
            List<string> items = new();

            if (index == 0){
                registry.Complete(prefix, items);
                return new Completion(text, start, items, null, -1);
            }

            if (!registry.TryGet(tokens[0].Text, out ConsoleCommand command))
                return new Completion(text, start, items, null, -1);
            int parameter = index - 1;
            if (parameter >= command.Parameters.Count) return new Completion(text, start, items, command, -1);
            command.Parameters[parameter].Suggest(prefix, context, items);
            return new Completion(text, start, items, command, parameter);
        }
    }
}
