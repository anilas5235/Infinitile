using System;
using System.Collections.Generic;
using UnityEngine;

namespace Engine.Scripts.Commands{
    /// <summary>
    ///     locate &lt;target&gt;: prints the coordinates of the nearest column of a biome or of river water, or of the highest peak nearby,
    ///     computed from the world generator without generating chunks (WorldGen v2 §13.6, DEC-160). The coordinates
    ///     go to <c>teleport</c>.
    /// </summary>
    public sealed class LocateCommand : ConsoleCommand{
        public LocateCommand() : base("locate", "Finds the nearest biome or river, or the highest peak nearby.", null,
            new LocateTargetParameter("target")){ }

        public override CommandResult Execute(CommandContext context, CommandArguments arguments){
            if (context.World is not ICommandLocator locator)
                return CommandResult.Fail("This world cannot locate places.");
            string target = arguments.Get<string>(0);
            if (!locator.Locate(target, context.Origin, out Vector3Int at, out string error)) return CommandResult.Fail(error);
            Vector3 from = context.Origin;
            float distance = Mathf.Sqrt((at.x - from.x) * (at.x - from.x) + (at.z - from.z) * (at.z - from.z));
            return CommandResult.Ok($"{target} at {at.x} {at.y} {at.z} ({distance:0} blocks away).");
        }
    }

    /// <summary>
    ///     goto &lt;target&gt;: finds the place the way <see cref="LocateCommand" /> does and teleports the player there, feet in
    ///     the middle of the found block.
    /// </summary>
    public sealed class GotoCommand : ConsoleCommand{
        public GotoCommand() : base("goto", "Teleports to the nearest biome or river, or the highest peak nearby.", null,
            new LocateTargetParameter("target")){ }

        public override CommandResult Execute(CommandContext context, CommandArguments arguments){
            if (context.World is not ICommandLocator locator)
                return CommandResult.Fail("This world cannot locate places.");
            if (context.Player == null) return CommandResult.Fail("There is no player to teleport.");
            string target = arguments.Get<string>(0);
            Vector3 from = context.Origin;
            if (!locator.Locate(target, from, out Vector3Int at, out string error)) return CommandResult.Fail(error);
            Vector3 feet = new(at.x + .5f, Mathf.Min(at.y, TeleportCommand.MaxY), at.z + .5f);
            context.Player.Teleport(feet);
            float distance = Mathf.Sqrt((feet.x - from.x) * (feet.x - from.x) + (feet.z - from.z) * (feet.z - from.z));
            return CommandResult.Ok($"Teleported to {target} at {at.x} {at.y} {at.z} ({distance:0} blocks).");
        }
    }

    /// <summary>A target of <see cref="LocateCommand" /> (case ignored); the value is the target as the world names it.</summary>
    public sealed class LocateTargetParameter : CommandParameter{
        public LocateTargetParameter(string name) : base(name){ }

        public override bool TryParse(string word, CommandContext context, out object value, out string error){
            value = null;
            if (context?.World is not ICommandLocator locator){
                error = "This world cannot locate places.";
                return false;
            }

            foreach (string target in locator.LocateTargets)
                if (string.Equals(target, word, StringComparison.OrdinalIgnoreCase)){
                    value = target;
                    error = null;
                    return true;
                }

            error = $"Unknown target '{word}'. Known: {string.Join(", ", locator.LocateTargets)}.";
            return false;
        }

        public override void Suggest(string prefix, CommandContext context, List<string> into){
            if (context?.World is not ICommandLocator locator) return;
            foreach (string target in locator.LocateTargets)
                if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    into.Add(target);
        }
    }
}
