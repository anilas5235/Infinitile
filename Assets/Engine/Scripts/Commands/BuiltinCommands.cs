using System.Text;
using UnityEngine;

namespace Engine.Scripts.Commands{
    /// <summary>help [command]: the list of all commands, or one command's usage, description and aliases.</summary>
    public sealed class HelpCommand : ConsoleCommand{
        public HelpCommand() : base("help", "Lists the commands, or shows how to use one.", null,
            new CommandNameParameter("command", true)){ }

        public override CommandResult Execute(CommandContext context, CommandArguments arguments){
            if (arguments.Has(0)){
                ConsoleCommand command = arguments.Get<ConsoleCommand>(0);
                StringBuilder text = new();
                text.Append(command.Usage).Append('\n').Append(command.Description);
                if (command.Aliases.Count > 0) text.Append("\nAlso: ").Append(string.Join(", ", command.Aliases));
                return CommandResult.Ok(text.ToString());
            }

            StringBuilder list = new();
            foreach (ConsoleCommand command in context.Registry.Commands){
                if (list.Length > 0) list.Append('\n');
                list.Append(command.Usage).Append("  ·  ").Append(command.Description);
            }

            return CommandResult.Ok(list.ToString());
        }
    }

    /// <summary>clear: empties the console's output.</summary>
    public sealed class ClearCommand : ConsoleCommand{
        public ClearCommand() : base("clear", "Empties the console.", null){ }

        public override CommandResult Execute(CommandContext context, CommandArguments arguments){
            context.Console?.Clear();
            return CommandResult.Ok();
        }
    }

    /// <summary>
    ///     teleport x y z (alias tp): moves the player's feet there; "~" is the player's own coordinate. A whole x or z
    ///     goes to the middle of its block.
    /// </summary>
    public sealed class TeleportCommand : ConsoleCommand{
        /// <summary>Highest feet height a teleport goes to.</summary>
        public const float MaxY = 1024f;

        public TeleportCommand() : base("teleport", "Moves the player to x y z.", new[]{ "tp" },
            new CoordinateParameter("x", 0, CoordinateKind.Position),
            new CoordinateParameter("y", 1, CoordinateKind.Position),
            new CoordinateParameter("z", 2, CoordinateKind.Position)){ }

        public override CommandResult Execute(CommandContext context, CommandArguments arguments){
            if (context.Player == null) return CommandResult.Fail("There is no player to teleport.");
            Vector3 target = new(arguments.Get<float>(0), arguments.Get<float>(1), arguments.Get<float>(2));
            // below the world a walking player would wait for ground that never comes
            if (target.y is < 0f or > MaxY)
                return CommandResult.Fail(
                    $"y {CoordinateParameter.Format(target.y)} is outside 0 to {CoordinateParameter.Format(MaxY)}.");
            context.Player.Teleport(target);
            return CommandResult.Ok($"Teleported to {Format(target)}.");
        }

        private static string Format(Vector3 p){
            return $"{CoordinateParameter.Format(p.x)} {CoordinateParameter.Format(p.y)} {CoordinateParameter.Format(p.z)}";
        }
    }

    /// <summary>
    ///     setvoxel x y z voxel: puts a base voxel (by full id, e.g. std:Stone; std:air removes) into a cell of a
    ///     loaded chunk, the way block placing does.
    /// </summary>
    public sealed class SetVoxelCommand : ConsoleCommand{
        public SetVoxelCommand() : base("setvoxel", "Puts a voxel at x y z, e.g. std:Stone.", null,
            new CoordinateParameter("x", 0, CoordinateKind.Block),
            new CoordinateParameter("y", 1, CoordinateKind.Block),
            new CoordinateParameter("z", 2, CoordinateKind.Block),
            new VoxelParameter("voxel")){ }

        public override CommandResult Execute(CommandContext context, CommandArguments arguments){
            ICommandWorld world = context.World;
            if (world == null) return CommandResult.Fail("There is no world.");
            Vector3Int cell = new(arguments.Get<int>(0), arguments.Get<int>(1), arguments.Get<int>(2));
            ushort id = arguments.Get<ushort>(3);
            string at = $"{cell.x} {cell.y} {cell.z}";
            if (cell.y < 0 || cell.y >= world.Height)
                return CommandResult.Fail($"y {cell.y} is outside the world (0 to {world.Height - 1}).");
            if (!world.IsLoaded(cell)) return CommandResult.Fail($"The chunk at {at} is not loaded.");
            if (!world.SetVoxel(id, cell)) return CommandResult.Fail($"Could not set the voxel at {at}.");
            world.Voxels.TryGetName(id, out string name);
            return CommandResult.Ok($"Set {name} at {at}.");
        }
    }
}
