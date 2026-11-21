using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Engine.Scripts.Commands{
    /// <summary>
    ///     One argument of a command: its name in the usage line, how a word is parsed into a value, and what
    ///     completion offers for it.
    /// </summary>
    public abstract class CommandParameter{
        protected CommandParameter(string name, bool optional = false){
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A parameter needs a name.", nameof(name));
            Name = name;
            Optional = optional;
        }

        public string Name{ get; }

        /// <summary>The argument may be left out; only trailing parameters may be optional.</summary>
        public bool Optional{ get; }

        /// <summary>The parameter in a usage line: &lt;name&gt;, or [name] when optional.</summary>
        public string Signature => Optional ? $"[{Name}]" : $"<{Name}>";

        /// <summary>Parses one word; on failure <paramref name="error" /> says what was wrong, without the usage.</summary>
        public abstract bool TryParse(string word, CommandContext context, out object value, out string error);

        /// <summary>Adds what completion offers for a word that starts with <paramref name="prefix" />.</summary>
        public virtual void Suggest(string prefix, CommandContext context, List<string> into){ }
    }

    /// <summary>How a coordinate is used: a point (teleport) or a voxel cell (whole numbers).</summary>
    public enum CoordinateKind : byte{
        /// <summary>Any number; a whole absolute x or z goes to the middle of its block (10 → 10.5).</summary>
        Position,

        /// <summary>A voxel cell: an absolute value must be whole, a relative one is floored.</summary>
        Block
    }

    /// <summary>
    ///     One axis of a position: a number, "~" (the player's own coordinate) or "~n" (n more). The value is a
    ///     <see cref="float" /> for <see cref="CoordinateKind.Position" />, an <see cref="int" /> for
    ///     <see cref="CoordinateKind.Block" />.
    /// </summary>
    public sealed class CoordinateParameter : CommandParameter{
        /// <summary>The biggest coordinate accepted; past it a float has no room for the fraction of a block.</summary>
        public const float Limit = 10_000_000f;

        /// <summary>Lifts a relative block coordinate off a feet height that sits a hair under a whole number.</summary>
        private const float BlockEpsilon = 1e-3f;

        private readonly int _axis;
        private readonly CoordinateKind _kind;

        /// <param name="name">Shown in the usage line, e.g. "x".</param>
        /// <param name="axis">0, 1 or 2 for x, y, z: the axis of the origin "~" reads.</param>
        /// <param name="kind">Point or voxel cell.</param>
        public CoordinateParameter(string name, int axis, CoordinateKind kind) : base(name){
            if (axis is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(axis));
            _axis = axis;
            _kind = kind;
        }

        public override bool TryParse(string word, CommandContext context, out object value, out string error){
            value = null;
            bool relative = word.StartsWith("~", StringComparison.Ordinal);
            string number = relative ? word[1..] : word;
            float offset = 0f;
            if ((!relative || number.Length > 0) && !TryNumber(number, out offset)){
                error = $"<{Name}> must be a number or ~, not '{word}'.";
                return false;
            }

            if (!relative && _kind == CoordinateKind.Block && offset != Mathf.Floor(offset)){
                error = $"<{Name}> must be a whole number for a block, not '{word}'.";
                return false;
            }

            float origin = relative ? context?.Origin[_axis] ?? 0f : 0f;
            float result = origin + offset;
            if (Mathf.Abs(result) > Limit){
                error = $"<{Name}> {Format(result)} is too far out (at most {Format(Limit)}).";
                return false;
            }

            error = null;
            if (_kind == CoordinateKind.Block){
                value = Mathf.FloorToInt(relative ? result + BlockEpsilon : result);
                return true;
            }

            bool whole = !relative && number.IndexOfAny(new[]{ '.', 'e', 'E' }) < 0;
            if (whole && _axis != 1) result += 0.5f;
            value = result;
            return true;
        }

        /// <summary>
        ///     Offers "~" for the rest of the position from this axis on: "~ ~ ~" for x, "~ ~" for y, "~" for z.
        /// </summary>
        public override void Suggest(string prefix, CommandContext context, List<string> into){
            if (prefix.Length > 0 && prefix != "~") return;
            into.Add(_axis switch{
                0 => "~ ~ ~",
                1 => "~ ~",
                _ => "~"
            });
        }

        private static bool TryNumber(string text, out float value){
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                   !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static string Format(float value){
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A base voxel by full id (package:Name, case ignored); the value is its <see cref="ushort" /> id.</summary>
    public sealed class VoxelParameter : CommandParameter{
        public VoxelParameter(string name) : base(name){ }

        public override bool TryParse(string word, CommandContext context, out object value, out string error){
            value = null;
            VoxelNameIndex voxels = context?.World?.Voxels;
            if (voxels == null){
                error = "There is no world with voxels.";
                return false;
            }

            if (voxels.TryGet(word, out ushort id, out _)){
                value = id;
                error = null;
                return true;
            }

            List<string> matches = new();
            if (word.IndexOf(':') < 0) voxels.FindByShortName(word, matches);
            error = matches.Count > 0
                ? $"Unknown voxel '{word}'. Did you mean {string.Join(" or ", matches)}?"
                : $"Unknown voxel '{word}'.";
            return false;
        }

        public override void Suggest(string prefix, CommandContext context, List<string> into){
            context?.World?.Voxels?.Complete(prefix, into);
        }
    }

    /// <summary>A command by name or alias; the value is the <see cref="ConsoleCommand" />.</summary>
    public sealed class CommandNameParameter : CommandParameter{
        public CommandNameParameter(string name, bool optional = false) : base(name, optional){ }

        public override bool TryParse(string word, CommandContext context, out object value, out string error){
            if (context?.Registry != null && context.Registry.TryGet(word, out ConsoleCommand command)){
                value = command;
                error = null;
                return true;
            }

            value = null;
            error = $"Unknown command '{word}'.";
            return false;
        }

        public override void Suggest(string prefix, CommandContext context, List<string> into){
            context?.Registry?.Complete(prefix, into);
        }
    }
}
