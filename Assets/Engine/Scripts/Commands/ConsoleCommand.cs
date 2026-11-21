using System;
using System.Collections.Generic;
using System.Linq;

namespace Engine.Scripts.Commands{
    /// <summary>The outcome of a command: success or failure, and a line for the console (none when null).</summary>
    public readonly struct CommandResult{
        public readonly bool Success;
        public readonly string Message;

        private CommandResult(bool success, string message){
            Success = success;
            Message = message;
        }

        public static CommandResult Ok(string message = null){
            return new CommandResult(true, message);
        }

        public static CommandResult Fail(string message){
            return new CommandResult(false, message);
        }
    }

    /// <summary>The parsed arguments of one call, in parameter order; a left-out optional argument is missing.</summary>
    public sealed class CommandArguments{
        private readonly IReadOnlyList<object> _values;

        public CommandArguments(IReadOnlyList<object> values){
            _values = values ?? Array.Empty<object>();
        }

        public int Count => _values.Count;

        /// <summary>The argument was given.</summary>
        public bool Has(int index){
            return index >= 0 && index < _values.Count;
        }

        public T Get<T>(int index){
            return (T)_values[index];
        }
    }

    /// <summary>
    ///     A console command: a name, aliases, a one-line description and its parameters, which give the usage line,
    ///     argument parsing and completion. Subclasses only run the parsed call. Register it in a
    ///     <see cref="CommandRegistry" />.
    /// </summary>
    public abstract class ConsoleCommand{
        private readonly CommandParameter[] _parameters;

        protected ConsoleCommand(string name, string description, IEnumerable<string> aliases,
            params CommandParameter[] parameters){
            Name = name;
            Description = description ?? "";
            Aliases = aliases?.ToArray() ?? Array.Empty<string>();
            _parameters = parameters ?? Array.Empty<CommandParameter>();
            bool optional = false;
            foreach (CommandParameter parameter in _parameters){
                if (optional && !parameter.Optional)
                    throw new ArgumentException($"Command '{name}': a required parameter follows an optional one.");
                optional |= parameter.Optional;
            }

            RequiredCount = _parameters.Count(p => !p.Optional);
        }

        /// <summary>The name typed to run it (case ignored).</summary>
        public string Name{ get; }

        /// <summary>Other names that run it.</summary>
        public IReadOnlyList<string> Aliases{ get; }

        /// <summary>What it does, in one line.</summary>
        public string Description{ get; }

        public IReadOnlyList<CommandParameter> Parameters => _parameters;

        /// <summary>The number of parameters that must be given.</summary>
        public int RequiredCount{ get; }

        /// <summary>The usage line: the name and every parameter's signature, e.g. "teleport &lt;x&gt; &lt;y&gt; &lt;z&gt;".</summary>
        public string Usage => _parameters.Length == 0
            ? Name
            : Name + " " + string.Join(" ", _parameters.Select(p => p.Signature));

        /// <summary>Runs the call; the arguments are parsed and their count checked.</summary>
        public abstract CommandResult Execute(CommandContext context, CommandArguments arguments);
    }
}
