using System;
using System.Collections.Generic;

namespace Engine.Scripts.Commands{
    /// <summary>
    ///     The console's commands by name and alias (case ignored). Mods and game code add their own with
    ///     <see cref="Register" />; a name or alias can belong to one command only.
    /// </summary>
    public sealed class CommandRegistry{
        private static readonly Comparer<ConsoleCommand> ByName =
            Comparer<ConsoleCommand>.Create((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        private static CommandRegistry _default;

        private readonly Dictionary<string, ConsoleCommand> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ConsoleCommand> _commands = new();

        /// <summary>The registry the game's console uses, with the built-in commands.</summary>
        public static CommandRegistry Default => _default ??= CreateWithBuiltins();

        /// <summary>The commands sorted by name.</summary>
        public IReadOnlyList<ConsoleCommand> Commands => _commands;

        /// <summary>A registry with help, clear, teleport, setvoxel and locate.</summary>
        public static CommandRegistry CreateWithBuiltins(){
            CommandRegistry registry = new();
            registry.Register(new HelpCommand());
            registry.Register(new ClearCommand());
            registry.Register(new TeleportCommand());
            registry.Register(new SetVoxelCommand());
            registry.Register(new LocateCommand());
            return registry;
        }

        /// <summary>Adds a command; throws when its name or an alias is empty, has whitespace or is taken.</summary>
        public void Register(ConsoleCommand command){
            if (command == null) throw new ArgumentNullException(nameof(command));
            List<string> names = new(){ command.Name };
            names.AddRange(command.Aliases);
            HashSet<string> own = new(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names){
                if (!IsValidName(name))
                    throw new ArgumentException($"'{name}' is not a valid command name (letters, digits, - _ : .).");
                if (_byName.TryGetValue(name, out ConsoleCommand other))
                    throw new ArgumentException($"The command name '{name}' is taken by '{other.Name}'.");
                if (!own.Add(name)) throw new ArgumentException($"Command '{command.Name}' lists '{name}' twice.");
            }

            foreach (string name in names) _byName[name] = command;
            int index = _commands.BinarySearch(command, ByName);
            _commands.Insert(index < 0 ? ~index : index, command);
        }

        /// <summary>Removes the command with this name or alias, with all its names.</summary>
        public bool Unregister(string nameOrAlias){
            if (!TryGet(nameOrAlias, out ConsoleCommand command)) return false;
            _byName.Remove(command.Name);
            foreach (string alias in command.Aliases) _byName.Remove(alias);
            _commands.Remove(command);
            return true;
        }

        public bool TryGet(string nameOrAlias, out ConsoleCommand command){
            command = null;
            return nameOrAlias != null && _byName.TryGetValue(nameOrAlias, out command);
        }

        /// <summary>Adds the names, then the aliases, that start with <paramref name="prefix" /> (case ignored).</summary>
        public void Complete(string prefix, List<string> into){
            prefix ??= "";
            foreach (ConsoleCommand command in _commands)
                if (command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    into.Add(command.Name);
            if (prefix.Length == 0) return;
            foreach (ConsoleCommand command in _commands)
            foreach (string alias in command.Aliases)
                if (alias.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    into.Add(alias);
        }

        private static bool IsValidName(string name){
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c is not ('-' or '_' or ':' or '.'))
                    return false;
            return true;
        }

    }
}
