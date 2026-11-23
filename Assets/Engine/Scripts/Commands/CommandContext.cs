using System.Collections.Generic;
using UnityEngine;

namespace Engine.Scripts.Commands{
    /// <summary>The world as commands see it: voxel names, and reading and setting voxels.</summary>
    public interface ICommandWorld{
        /// <summary>The base voxels by full id (package:Name), for parsing and completion.</summary>
        VoxelNameIndex Voxels{ get; }

        /// <summary>Height of the world in voxels; y runs from 0 to Height - 1.</summary>
        int Height{ get; }

        /// <summary>True once the chunk column holding the position has its voxel data.</summary>
        bool IsLoaded(Vector3Int position);

        /// <summary>Sets one voxel the way block placing does (light and meshes follow); false when it could not.</summary>
        bool SetVoxel(ushort id, Vector3Int position);
    }

    /// <summary>
    ///     Finding places in the generated world without generating chunks (WorldGen v2 §13.6): implemented by
    ///     worlds that can (the density generator); <see cref="LocateCommand" /> looks for it on the world.
    /// </summary>
    public interface ICommandLocator{
        /// <summary>What can be found: the world's biome names, and "peak".</summary>
        IReadOnlyList<string> LocateTargets{ get; }

        /// <summary>
        ///     The nearest place of <paramref name="target" /> to <paramref name="from" />: x and z of the column, y one
        ///     block above its top terrain voxel. False with <paramref name="error" /> when there is none.
        /// </summary>
        bool Locate(string target, Vector3 from, out Vector3Int position, out string error);
    }

    /// <summary>The player as commands see it.</summary>
    public interface ICommandPlayer{
        /// <summary>The player's feet in world space; "~" coordinates are relative to it.</summary>
        Vector3 Position{ get; }

        /// <summary>Moves the player's feet to the position.</summary>
        void Teleport(Vector3 feet);
    }

    /// <summary>
    ///     What a command runs against: the registry it came from, the console it writes to, and the world and player
    ///     (either may be missing, e.g. before the world exists or in tests).
    /// </summary>
    public sealed class CommandContext{
        public CommandContext(CommandRegistry registry, CommandConsole console, ICommandWorld world, ICommandPlayer player){
            Registry = registry;
            Console = console;
            World = world;
            Player = player;
        }

        public CommandRegistry Registry{ get; }
        public CommandConsole Console{ get; }
        public ICommandWorld World{ get; }
        public ICommandPlayer Player{ get; }

        /// <summary>Where "~" points: the player's feet, the world origin without a player.</summary>
        public Vector3 Origin => Player?.Position ?? Vector3.zero;

        /// <summary>Writes an info line to the console.</summary>
        public void Print(string text){
            Console?.Print(text);
        }
    }
}
