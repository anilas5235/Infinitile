using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Engine.Scripts.Commands;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The command console without the game: the line tokenizer, the registry (names, aliases, conflicts, mod
    ///     commands), coordinate and voxel arguments, completion and Tab cycling, the history, help and clear, and the
    ///     checks of teleport and setvoxel, on a fake world and player.
    /// </summary>
    public class CommandConsoleTests{
        private static readonly (ushort, string)[] Voxels = {
            (0, "std:air"), (1, "std:Stone"), (2, "std:Sand"), (3, "std:Grass"), (4, "mod:Stone")
        };

        private FakePlayer _player;
        private FakeWorld _world;

        [SetUp]
        public void SetUp(){
            _world = new FakeWorld();
            _player = new FakePlayer{ Position = new Vector3(10.25f, 64f, -3.5f) };
        }

        private CommandConsole NewConsole(CommandRegistry registry = null){
            return new CommandConsole(registry ?? CommandRegistry.CreateWithBuiltins()){
                World = _world, Player = _player
            };
        }

        private static string LastLine(CommandConsole console){
            return console.Lines.Count > 0 ? console.Lines[^1].Text : null;
        }

        private static ConsoleLineKind LastKind(CommandConsole console){
            return console.Lines[^1].Kind;
        }

        // ── Tokenizer ────────────────────────────────────────────────────────

        [Test]
        public void Parse_SkipsSlashAndSplitsAtWhitespace(){
            CommandLine line = CommandLine.Parse("  /tp 1  ~2\t3");
            Assert.AreEqual(3, line.BodyStart);
            CollectionAssert.AreEqual(new[]{ "tp", "1", "~2", "3" }, line.Tokens.Select(t => t.Text).ToArray());
            Assert.AreEqual(3, line.Tokens[0].Start);
            Assert.AreEqual(9, line.Tokens[2].Start);
            Assert.AreEqual("tp", line.CommandName);
            Assert.IsFalse(line.EndsWithSpace);
            Assert.IsTrue(CommandLine.Parse("tp ").EndsWithSpace);
        }

        [Test]
        public void Parse_EmptyAndSlashOnly_HaveNoTokens(){
            Assert.AreEqual(0, CommandLine.Parse("").Tokens.Count);
            Assert.AreEqual(0, CommandLine.Parse("/").Tokens.Count);
            Assert.IsFalse(CommandLine.Parse("/").EndsWithSpace);
            Assert.AreEqual("", CommandLine.Parse("   ").CommandName);
        }

        // ── Registry ─────────────────────────────────────────────────────────

        [Test]
        public void Registry_FindsNamesAndAliases_CaseIgnored(){
            CommandRegistry registry = CommandRegistry.CreateWithBuiltins();
            Assert.IsTrue(registry.TryGet("TP", out ConsoleCommand tp));
            Assert.IsTrue(registry.TryGet("Teleport", out ConsoleCommand teleport));
            Assert.AreSame(tp, teleport);
            Assert.IsFalse(registry.TryGet("fly", out _));
            CollectionAssert.AreEqual(new[]{ "clear", "goto", "help", "locate", "setvoxel", "teleport" },
                registry.Commands.Select(c => c.Name).ToArray());
        }

        [Test]
        public void Registry_RefusesTakenInvalidAndRepeatedNames(){
            CommandRegistry registry = CommandRegistry.CreateWithBuiltins();
            Assert.Throws<ArgumentException>(() => registry.Register(new TestCommand("TELEPORT")));
            Assert.Throws<ArgumentException>(() => registry.Register(new TestCommand("go", "tp")));
            Assert.Throws<ArgumentException>(() => registry.Register(new TestCommand("go to")));
            Assert.Throws<ArgumentException>(() => registry.Register(new TestCommand("?")));
            Assert.Throws<ArgumentException>(() => registry.Register(new TestCommand("go", "Go")));
            Assert.IsFalse(registry.TryGet("go", out _), "a refused command adds none of its names");
            Assert.Throws<ArgumentNullException>(() => registry.Register(null));
        }

        [Test]
        public void Registry_Unregister_RemovesNameAndAliases(){
            CommandRegistry registry = CommandRegistry.CreateWithBuiltins();
            Assert.IsTrue(registry.Unregister("tp"));
            Assert.IsFalse(registry.TryGet("teleport", out _));
            Assert.IsFalse(registry.TryGet("tp", out _));
            Assert.IsFalse(registry.Unregister("tp"));
            registry.Register(new TestCommand("tp"));
            Assert.IsTrue(registry.TryGet("tp", out ConsoleCommand command));
            Assert.AreEqual("tp", command.Name);
        }

        [Test]
        public void Command_RequiredAfterOptional_Throws(){
            Assert.Throws<ArgumentException>(() => new TestCommand("bad", null,
                new CommandNameParameter("a", true), new CommandNameParameter("b")));
        }

        [Test]
        public void ModCommand_RunsWithItsArguments(){
            CommandRegistry registry = CommandRegistry.CreateWithBuiltins();
            TestCommand echo = new("echo", new[]{ "say" }, new VoxelParameter("voxel")){
                Run = (_, args) => CommandResult.Ok($"got {args.Get<ushort>(0)}")
            };
            registry.Register(echo);
            CommandConsole console = NewConsole(registry);
            Assert.IsTrue(console.Submit("/say std:sand"));
            Assert.AreEqual("got 2", LastLine(console));
            Assert.AreEqual("echo <voxel>", echo.Usage);
        }

        // ── Coordinates ──────────────────────────────────────────────────────

        private object ParseCoordinate(int axis, CoordinateKind kind, string word, out string error){
            CoordinateParameter parameter = new("x", axis, kind);
            CommandContext context = new(null, null, _world, _player);
            return parameter.TryParse(word, context, out object value, out error) ? value : null;
        }

        [Test]
        public void Position_RelativeAndWholeNumbers(){
            Assert.AreEqual(10.25f, ParseCoordinate(0, CoordinateKind.Position, "~", out _));
            Assert.AreEqual(65f, ParseCoordinate(1, CoordinateKind.Position, "~1", out _));
            Assert.AreEqual(-5.5f, ParseCoordinate(2, CoordinateKind.Position, "~-2", out _));
            // a whole x or z goes to the middle of its block, y never
            Assert.AreEqual(5.5f, ParseCoordinate(0, CoordinateKind.Position, "5", out _));
            Assert.AreEqual(-2.5f, ParseCoordinate(2, CoordinateKind.Position, "-3", out _));
            Assert.AreEqual(5f, ParseCoordinate(1, CoordinateKind.Position, "5", out _));
            Assert.AreEqual(5f, ParseCoordinate(0, CoordinateKind.Position, "5.0", out _));
            Assert.AreEqual(5.25f, ParseCoordinate(0, CoordinateKind.Position, "5.25", out _));
        }

        [Test]
        public void Block_WholeNumbersAndFlooredRelative(){
            Assert.AreEqual(10, ParseCoordinate(0, CoordinateKind.Block, "~", out _));
            Assert.AreEqual(63, ParseCoordinate(1, CoordinateKind.Block, "~-1", out _));
            Assert.AreEqual(-4, ParseCoordinate(2, CoordinateKind.Block, "~", out _));
            Assert.AreEqual(-7, ParseCoordinate(0, CoordinateKind.Block, "-7", out _));
            // feet a hair under a whole height stand on that block
            _player.Position = new Vector3(0f, 63.9996f, 0f);
            Assert.AreEqual(64, ParseCoordinate(1, CoordinateKind.Block, "~", out _));
        }

        [Test]
        public void Coordinate_BadWords_SayWhy(){
            Assert.IsNull(ParseCoordinate(0, CoordinateKind.Block, "1.5", out string error));
            StringAssert.Contains("whole number", error);
            Assert.IsNull(ParseCoordinate(0, CoordinateKind.Position, "abc", out error));
            StringAssert.Contains("must be a number or ~", error);
            Assert.IsNull(ParseCoordinate(0, CoordinateKind.Position, "~x", out error));
            StringAssert.Contains("'~x'", error);
            Assert.IsNull(ParseCoordinate(0, CoordinateKind.Position, "NaN", out _));
            Assert.IsNull(ParseCoordinate(0, CoordinateKind.Position, "1e40", out _));
            Assert.IsNull(ParseCoordinate(0, CoordinateKind.Position, "20000000", out error));
            StringAssert.Contains("too far out", error);
        }

        // ── Voxels ───────────────────────────────────────────────────────────

        [Test]
        public void Voxel_FullIdWithCaseIgnored(){
            VoxelParameter parameter = new("voxel");
            CommandContext context = new(null, null, _world, null);
            Assert.IsTrue(parameter.TryParse("STD:stone", context, out object value, out _));
            Assert.AreEqual((ushort)1, value);
            Assert.IsTrue(parameter.TryParse("std:AIR", context, out value, out _));
            Assert.AreEqual((ushort)0, value);
        }

        [Test]
        public void Voxel_Unknown_SuggestsFullIds(){
            VoxelParameter parameter = new("voxel");
            CommandContext context = new(null, null, _world, null);
            Assert.IsFalse(parameter.TryParse("stone", context, out _, out string error));
            Assert.AreEqual("Unknown voxel 'stone'. Did you mean mod:Stone or std:Stone?", error);
            Assert.IsFalse(parameter.TryParse("std:Lava", context, out _, out error));
            Assert.AreEqual("Unknown voxel 'std:Lava'.", error);
            Assert.IsFalse(parameter.TryParse("std:Stone", new CommandContext(null, null, null, null), out _,
                out error));
            Assert.AreEqual("There is no world with voxels.", error);
        }

        [Test]
        public void VoxelIndex_SkipsNamesWithoutPackageAndRepeats(){
            VoxelNameIndex index = new(new (ushort, string)[]{ (1, "std:Stone"), (2, "Loose"), (3, "STD:STONE"), (4, "") });
            Assert.AreEqual(1, index.Count);
            Assert.IsTrue(index.TryGetName(1, out string name));
            Assert.AreEqual("std:Stone", name);
            Assert.IsFalse(index.TryGetName(2, out _));
        }

        // ── Completion ───────────────────────────────────────────────────────

        private static string[] Items(CommandConsole console, string text){
            return console.Suggest(text).Items.ToArray();
        }

        [Test]
        public void Complete_CommandNamesThenAliases(){
            CommandConsole console = NewConsole();
            CollectionAssert.AreEqual(new[]{ "clear", "goto", "help", "locate", "setvoxel", "teleport" }, Items(console, ""));
            CollectionAssert.AreEqual(new[]{ "teleport", "tp" }, Items(console, "/t"));
            CollectionAssert.AreEqual(new[]{ "help" }, Items(console, "HE"));
            Assert.AreEqual(0, Items(console, "fly").Length);
        }

        [Test]
        public void Complete_Arguments(){
            CommandConsole console = NewConsole();
            CollectionAssert.AreEqual(new[]{ "~ ~ ~" }, Items(console, "tp "));
            CollectionAssert.AreEqual(new[]{ "~ ~" }, Items(console, "tp 1 ~"));
            Assert.AreEqual(0, Items(console, "tp 1").Length);
            CollectionAssert.AreEqual(new[]{ "std:Sand", "std:Stone" }, Items(console, "setvoxel 0 0 0 std:s"));
            // by the name after the package
            CollectionAssert.AreEqual(new[]{ "mod:Stone", "std:Stone" }, Items(console, "setvoxel 0 0 0 sto"));
            CollectionAssert.AreEqual(new[]{ "teleport", "tp" }, Items(console, "help t"));
            Assert.AreEqual(0, Items(console, "tp 1 2 3 ").Length, "nothing past the last parameter");
            Assert.AreEqual(0, Items(console, "fly ").Length, "nothing for an unknown command");
        }

        [Test]
        public void Complete_KnowsCommandAndParameter(){
            CommandConsole console = NewConsole();
            Completion completion = console.Suggest("setvoxel 1 2 3 std:");
            Assert.IsTrue(console.Registry.TryGet("setvoxel", out ConsoleCommand setVoxel));
            Assert.AreSame(setVoxel, completion.Command);
            Assert.AreEqual(3, completion.ParameterIndex);
            Assert.AreEqual(15, completion.ReplaceStart);
            Assert.AreEqual(4, completion.Items.Count, "every std voxel");
        }

        [Test]
        public void Tab_SingleItemGetsSpace_KeepsSlash(){
            CommandConsole console = NewConsole();
            Assert.AreEqual("/help ", console.Tab("/hel"));
            Assert.AreEqual("tp ~ ~ ~ ", console.Tab("tp "));
            Assert.AreEqual("fly", console.Tab("fly"), "unchanged when nothing fits");
        }

        [Test]
        public void Tab_Again_CyclesItems(){
            CommandConsole console = NewConsole();
            string first = console.Tab("setvoxel 0 0 0 std:s");
            Assert.AreEqual("setvoxel 0 0 0 std:Sand", first);
            Assert.AreEqual(0, console.CompletionIndex);
            Assert.IsNotNull(console.ActiveCompletion(first));
            string second = console.Tab(first);
            Assert.AreEqual("setvoxel 0 0 0 std:Stone", second);
            Assert.AreEqual("setvoxel 0 0 0 std:Sand", console.Tab(second), "round");
            // an edited line completes anew
            Assert.IsNull(console.ActiveCompletion("setvoxel 0 0 0 std:G"));
            Assert.AreEqual("setvoxel 0 0 0 std:Grass ", console.Tab("setvoxel 0 0 0 std:G"));
        }

        // ── Running ──────────────────────────────────────────────────────────

        [Test]
        public void Submit_EchoesAndRecordsHistory(){
            CommandConsole console = NewConsole();
            Assert.IsTrue(console.Submit("  "));
            Assert.IsTrue(console.Submit("/"));
            Assert.AreEqual(0, console.Lines.Count, "an empty line does nothing");
            console.Submit("help tp");
            Assert.AreEqual(ConsoleLineKind.Input, console.Lines[0].Kind);
            Assert.AreEqual("help tp", console.Lines[0].Text);
            CollectionAssert.AreEqual(new[]{ "help tp" }, console.History.ToArray());
        }

        [Test]
        public void Submit_Errors(){
            CommandConsole console = NewConsole();
            Assert.IsFalse(console.Submit("fly"));
            Assert.AreEqual("Unknown command 'fly'. Type help for the list of commands.", LastLine(console));
            Assert.AreEqual(ConsoleLineKind.Error, LastKind(console));
            Assert.IsFalse(console.Submit("tp 1 2"));
            Assert.AreEqual("Missing <z>. Usage: teleport <x> <y> <z>", LastLine(console));
            Assert.IsFalse(console.Submit("tp 1 2 3 4"));
            Assert.AreEqual("Too many arguments. Usage: teleport <x> <y> <z>", LastLine(console));
            Assert.IsFalse(console.Submit("tp a 2 3"));
            Assert.AreEqual("<x> must be a number or ~, not 'a'.", LastLine(console));
            Assert.IsFalse(console.Submit("help fly"));
            Assert.AreEqual("Unknown command 'fly'.", LastLine(console));
        }

        [Test]
        public void Submit_CommandThatThrows_PrintsError(){
            CommandRegistry registry = CommandRegistry.CreateWithBuiltins();
            registry.Register(new TestCommand("boom"){ Run = (_, _) => throw new InvalidOperationException("bad") });
            CommandConsole console = NewConsole(registry);
            LogAssert.Expect(LogType.Exception, new Regex("bad"));
            Assert.IsFalse(console.Submit("boom"));
            Assert.AreEqual("boom failed: bad", LastLine(console));
        }

        [Test]
        public void Output_SplitsLinesAndKeepsTheNewest(){
            float time = 3f;
            CommandConsole console = new(CommandRegistry.CreateWithBuiltins(), () => time);
            int changes = 0;
            console.LinesChanged += () => changes++;
            console.Print("a\nb");
            Assert.AreEqual(2, console.Lines.Count);
            Assert.AreEqual(1, changes);
            Assert.AreEqual(3f, console.Lines[1].Time);
            for (int i = 0; i < CommandConsole.MaxLines + 5; i++) console.Print($"line {i}");
            Assert.AreEqual(CommandConsole.MaxLines, console.Lines.Count);
            Assert.AreEqual($"line {CommandConsole.MaxLines + 4}", LastLine(console));
        }

        [Test]
        public void History_StepsBackAndForthToTheDraft(){
            CommandConsole console = NewConsole();
            console.Submit("help");
            console.Submit("tp 1 2 3");
            console.Submit("tp 1 2 3");
            CollectionAssert.AreEqual(new[]{ "help", "tp 1 2 3" }, console.History.ToArray(), "no repeats in a row");
            Assert.AreEqual("tp 1 2 3", console.HistoryPrevious("draft"));
            Assert.AreEqual("help", console.HistoryPrevious("tp 1 2 3"));
            Assert.AreEqual("help", console.HistoryPrevious("help"), "stays on the oldest");
            Assert.AreEqual("tp 1 2 3", console.HistoryNext("help"));
            Assert.AreEqual("draft", console.HistoryNext("tp 1 2 3"));
            Assert.AreEqual("draft", console.HistoryNext("draft"));
            Assert.AreEqual("x", NewConsole().HistoryPrevious("x"), "no history");
        }

        [Test]
        public void Help_ListsCommandsAndShowsOne(){
            CommandConsole console = NewConsole();
            console.Submit("help");
            string[] lines = console.Lines.Skip(1).Select(l => l.Text).ToArray();
            Assert.AreEqual(6, lines.Length);
            Assert.AreEqual("teleport <x> <y> <z>  ·  Moves the player to x y z.", lines[5]);
            Assert.AreEqual("locate <target>  ·  Finds the nearest biome or river, or the highest peak nearby.", lines[3]);
            Assert.AreEqual("help [command]  ·  Lists the commands, or shows how to use one.", lines[2]);
            Assert.AreEqual("goto <target>  ·  Teleports to the nearest biome or river, or the highest peak nearby.", lines[1]);

            console.Clear();
            console.Submit("help TP");
            CollectionAssert.AreEqual(new[]{ "teleport <x> <y> <z>", "Moves the player to x y z.", "Also: tp" },
                console.Lines.Skip(1).Select(l => l.Text).ToArray());
        }

        [Test]
        public void Clear_EmptiesTheOutput(){
            CommandConsole console = NewConsole();
            console.Submit("help");
            Assert.IsTrue(console.Submit("clear"));
            Assert.AreEqual(0, console.Lines.Count);
            Assert.AreEqual(2, console.History.Count, "the history stays");
        }

        // ── teleport ─────────────────────────────────────────────────────────

        [Test]
        public void Teleport_MovesTheFeet(){
            CommandConsole console = NewConsole();
            Assert.IsTrue(console.Submit("tp 5 70 ~"));
            Assert.AreEqual(new Vector3(5.5f, 70f, -3.5f), _player.Teleported);
            Assert.AreEqual("Teleported to 5.5 70 -3.5.", LastLine(console));
            Assert.IsTrue(console.Submit("teleport ~ ~10 ~1.25"));
            Assert.AreEqual(new Vector3(5.5f, 80f, -2.25f), _player.Teleported);
        }

        [Test]
        public void Teleport_RefusesOutsideHeightAndWithoutPlayer(){
            CommandConsole console = NewConsole();
            Assert.IsFalse(console.Submit("tp 0 -1 0"));
            Assert.AreEqual("y -1 is outside 0 to 1024.", LastLine(console));
            Assert.IsFalse(console.Submit("tp 0 1024.5 0"));
            Assert.IsNull(_player.Teleported);
            console.Player = null;
            Assert.IsFalse(console.Submit("tp 0 70 0"));
            Assert.AreEqual("There is no player to teleport.", LastLine(console));
        }

        // ── setvoxel ─────────────────────────────────────────────────────────

        [Test]
        public void SetVoxel_PutsTheVoxel(){
            CommandConsole console = NewConsole();
            Assert.IsTrue(console.Submit("setvoxel 1 2 3 std:stone"));
            Assert.AreEqual((ushort)1, _world.Set[new Vector3Int(1, 2, 3)]);
            Assert.AreEqual("Set std:Stone at 1 2 3.", LastLine(console));
            Assert.IsTrue(console.Submit("setvoxel ~ ~-1 ~ std:air"));
            Assert.AreEqual((ushort)0, _world.Set[new Vector3Int(10, 63, -4)]);
        }

        [Test]
        public void SetVoxel_RefusesOutsideWorldUnloadedAndUnknown(){
            CommandConsole console = NewConsole();
            Assert.IsFalse(console.Submit("setvoxel 1 256 3 std:Stone"));
            Assert.AreEqual("y 256 is outside the world (0 to 255).", LastLine(console));
            Assert.IsFalse(console.Submit("setvoxel 1 -1 3 std:Stone"));
            Assert.IsFalse(console.Submit("setvoxel -1 2 3 std:Stone"));
            Assert.AreEqual("The chunk at -1 2 3 is not loaded.", LastLine(console));
            Assert.IsFalse(console.Submit("setvoxel 1 2 3 stone"));
            StringAssert.StartsWith("Unknown voxel 'stone'.", LastLine(console));
            Assert.IsFalse(console.Submit("setvoxel 1 2.5 3 std:Stone"));
            Assert.IsFalse(console.Submit("setvoxel 1 2 std:Stone"));
            Assert.AreEqual("Missing <voxel>. Usage: setvoxel <x> <y> <z> <voxel>", LastLine(console));
            _world.Refuse = true;
            Assert.IsFalse(console.Submit("setvoxel 1 2 3 std:Stone"));
            Assert.AreEqual("Could not set the voxel at 1 2 3.", LastLine(console));
            Assert.AreEqual(0, _world.Set.Count);
            console.World = null;
            Assert.IsFalse(console.Submit("setvoxel 1 2 3 std:Stone"));
            Assert.AreEqual("There is no world with voxels.", LastLine(console));
        }

        // ── Locate ───────────────────────────────────────────────────────────

        [Test]
        public void Locate_PrintsTheNearestPlace_CaseIgnored(){
            CommandConsole console = NewConsole();
            console.World = new FakeLocatorWorld();
            Assert.IsTrue(console.Submit("locate desert"));
            Assert.AreEqual("Desert at 310 71 -4 (300 blocks away).", LastLine(console));
            Assert.IsTrue(console.Submit("/locate peak"));
            StringAssert.StartsWith("peak at 10 190 997", LastLine(console));
        }

        [Test]
        public void Locate_RefusesUnknownTargetsMissingPlacesAndWorldsWithoutLocator(){
            CommandConsole console = NewConsole();
            console.World = new FakeLocatorWorld();
            Assert.IsFalse(console.Submit("locate Volcano"));
            Assert.AreEqual("Unknown target 'Volcano'. Known: Desert, Ocean, peak.", LastLine(console));
            Assert.IsFalse(console.Submit("locate Ocean"));
            Assert.AreEqual("No Ocean within 20000 blocks.", LastLine(console));
            console.World = _world;
            Assert.IsFalse(console.Submit("locate Desert"));
            Assert.AreEqual("This world cannot locate places.", LastLine(console));
        }

        [Test]
        public void Locate_CompletesTargets(){
            CommandConsole console = NewConsole();
            console.World = new FakeLocatorWorld();
            CollectionAssert.AreEqual(new[]{ "Desert", "Ocean", "peak" }, Items(console, "locate "));
            CollectionAssert.AreEqual(new[]{ "Ocean" }, Items(console, "locate o"));
        }

        [Test]
        public void Goto_TeleportsToTheLocatedPlace(){
            CommandConsole console = NewConsole();
            console.World = new FakeLocatorWorld();
            Assert.IsTrue(console.Submit("goto peak"));
            Assert.AreEqual(new Vector3(10.5f, 190f, 997.5f), _player.Teleported);
            StringAssert.StartsWith("Teleported to peak at 10 190 997 (", LastLine(console));
            CollectionAssert.AreEqual(new[]{ "Desert", "Ocean", "peak" }, Items(console, "goto "));
        }

        [Test]
        public void Goto_RefusesMissingPlacesAndWorldsWithoutLocator(){
            CommandConsole console = NewConsole();
            console.World = new FakeLocatorWorld();
            Assert.IsFalse(console.Submit("goto Ocean"));
            Assert.AreEqual("No Ocean within 20000 blocks.", LastLine(console));
            Assert.IsNull(_player.Teleported);
            console.World = _world;
            Assert.IsFalse(console.Submit("goto Desert"));
            Assert.AreEqual("This world cannot locate places.", LastLine(console));
        }

        // ── Fakes ────────────────────────────────────────────────────────────

        /// <summary>A world whose chunks with x ≥ 0 are loaded.</summary>
        private sealed class FakeWorld : ICommandWorld{
            public readonly Dictionary<Vector3Int, ushort> Set = new();
            public bool Refuse;
            public VoxelNameIndex Voxels{ get; } = new(CommandConsoleTests.Voxels);
            public int Height => 256;

            public bool IsLoaded(Vector3Int position){
                return position.x >= 0;
            }

            public bool SetVoxel(ushort id, Vector3Int position){
                if (Refuse) return false;
                Set[position] = id;
                return true;
            }
        }

        /// <summary>A world with Desert 300 blocks east of the player, no Ocean, and a peak.</summary>
        private sealed class FakeLocatorWorld : ICommandWorld, ICommandLocator{
            public VoxelNameIndex Voxels{ get; } = new(CommandConsoleTests.Voxels);
            public int Height => 256;
            public IReadOnlyList<string> LocateTargets{ get; } = new[]{ "Desert", "Ocean", "peak" };

            public bool IsLoaded(Vector3Int position){
                return true;
            }

            public bool SetVoxel(ushort id, Vector3Int position){
                return false;
            }

            public bool Locate(string target, Vector3 from, out Vector3Int position, out string error){
                error = null;
                position = default;
                switch (target){
                    case "Desert":
                        position = new Vector3Int(Mathf.RoundToInt(from.x) + 300, 71, Mathf.RoundToInt(from.z));
                        return true;
                    case "peak":
                        position = new Vector3Int(10, 190, 997);
                        return true;
                    default:
                        error = $"No {target} within 20000 blocks.";
                        return false;
                }
            }
        }

        private sealed class FakePlayer : ICommandPlayer{
            public Vector3? Teleported;
            public Vector3 Position{ get; set; }

            public void Teleport(Vector3 feet){
                Teleported = feet;
                Position = feet;
            }
        }

        private sealed class TestCommand : ConsoleCommand{
            public Func<CommandContext, CommandArguments, CommandResult> Run = (_, _) => CommandResult.Ok();

            public TestCommand(string name, string[] aliases = null, params CommandParameter[] parameters)
                : base(name, "A test command.", aliases, parameters){ }

            public TestCommand(string name, string alias) : this(name, new[]{ alias }){ }

            public override CommandResult Execute(CommandContext context, CommandArguments arguments){
                return Run(context, arguments);
            }
        }
    }
}
