using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Collections;
using NUnit.Framework;
using Unity.Collections;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Randomised model test, every Set branch and the structural invariants of
    ///     <see cref="UnsafeIntervalList{T}" />.
    /// </summary>
    public class UnsafeIntervalListTests{
        private const int ListLength = 32 * 256 * 32;
        private const int RandomOps = 20000;
        private const int RandomSeed = 20261017;

        private bool _hasList;
        private UnsafeIntervalList<ushort> _list;

        [TearDown]
        public void TearDown(){
            if (_hasList) _list.Dispose();
            _hasList = false;
        }

        private void Create(params (ushort value, int count)[] runs){
            _list = new UnsafeIntervalList<ushort>(16, Allocator.Persistent);
            _hasList = true;
            foreach ((ushort value, int count) in runs) _list.AddInterval(value, count);
        }

        private List<(ushort value, int count)> Nodes(){
            List<(ushort, int)> nodes = new();
            foreach (UnsafeIntervalList<ushort>.Node node in _list.Internal) nodes.Add((node.Value, node.Count));
            return nodes;
        }

        private static int RunCount(ushort[] model){
            int runs = 1;
            for (int i = 1; i < model.Length; i++)
                if (model[i] != model[i - 1])
                    runs++;
            return runs;
        }

        private void AssertNodes(params (ushort value, int count)[] expected){
            CollectionAssert.AreEqual(expected, Nodes(), _list.ToString());
        }

        private void AssertEqualsModel(ushort[] model){
            Assert.AreEqual(model.Length, _list.Length);
            for (int i = 0; i < model.Length; i++)
                if (model[i] != _list.Get(i))
                    Assert.Fail($"Mismatch at index {i}: model {model[i]}, list {_list.Get(i)}");
        }

        /// <summary>Invariants: no empty node, no equal neighbours, strictly increasing end counts.</summary>
        private void AssertInvariants(string context){
            // Runs after every random op over thousands of nodes: the message (which prints the whole list) is
            // only built on failure, otherwise the check is quadratic per op and never finishes.
            int previousCount = 0;
            ushort previousValue = 0;
            int i = 0;
            foreach (UnsafeIntervalList<ushort>.Node node in _list.Internal){
                if (node.Count <= previousCount) Assert.Fail($"{context}: node {i} is empty or counts are not increasing\n{_list}");
                if (i > 0 && node.Value == previousValue) Assert.Fail($"{context}: nodes {i - 1} and {i} have equal values\n{_list}");
                previousCount = node.Count;
                previousValue = node.Value;
                i++;
            }

            if (i > 0) Assert.AreEqual(_list.Length, previousCount, $"{context}: last node must end at Length");
        }

        private static int PickIndex(Random rng, int previous){
            int mode = rng.Next(100);
            if (mode < 2) return 0;
            if (mode < 4) return ListLength - 1;
            if (mode < 40) return Math.Clamp(previous + rng.Next(-3, 4), 0, ListLength - 1);
            if (mode < 85) return rng.Next(0, 4096);
            return rng.Next(0, ListLength);
        }

        private void RunRandomSets(Action<ushort[], int, string> afterEachOp){
            Random rng = new(RandomSeed);
            ushort[] model = new ushort[ListLength];
            Create((0, ListLength));
            int index = 0;
            for (int op = 0; op < RandomOps; op++){
                index = PickIndex(rng, index);
                ushort value = (ushort)rng.Next(0, 4);
                bool expectChange = model[index] != value;
                bool changed = _list.Set(index, value);
                Assert.AreEqual(expectChange, changed, $"op {op}: Set({index}, {value}) return value");
                model[index] = value;
                afterEachOp(model, index, $"op {op}: Set({index}, {value})");
            }

            AssertEqualsModel(model);
        }

        #region Randomised

        [Test]
        public void RandomSets_GetMatchesModelAfterEveryOperation(){
            RunRandomSets((model, index, context) => {
                for (int d = -3; d <= 3; d++){
                    int i = index + d;
                    if ((uint)i >= ListLength) continue;
                    Assert.AreEqual(model[i], _list.Get(i), $"{context}: Get({i})");
                }

                Assert.AreEqual(ListLength, _list.Length, context);
            });
        }

        [Test]
        public void RandomSets_NodeCountNeverExceedsValueChangesPlusOne(){
            int op = 0;
            RunRandomSets((model, _, context) => {
                if (++op % 500 != 0) return;
                Assert.LessOrEqual(_list.CompressedLength, RunCount(model), $"{context}: runs are not merged\n{_list}");
            });
            Assert.AreEqual(RunCount(RandomModel()), _list.CompressedLength);
        }

        private static ushort[] RandomModel(){
            // Re-plays the same sequence so the final run count can be compared without keeping state.
            Random rng = new(RandomSeed);
            ushort[] model = new ushort[ListLength];
            int index = 0;
            for (int op = 0; op < RandomOps; op++){
                index = PickIndex(rng, index);
                model[index] = (ushort)rng.Next(0, 4);
            }

            return model;
        }

        #endregion

        #region Invariants

        [Test]
        public void RandomSets_InvariantsHoldAfterEveryOperation(){
            RunRandomSets((_, _, context) => AssertInvariants(context));
        }

        [Test]
        public void AddInterval_EqualNeighbouringRuns_AreMerged(){
            // AddInterval must merge a run that equals the previous run.
            Create((1, 3), (1, 2), (2, 4));
            Assert.AreEqual(9, _list.Length);
            AssertInvariants("AddInterval equal runs");
            AssertNodes((1, 5), (2, 9));
        }

        [Test]
        public void AddInterval_CountZero_AddsNoNode(){
            // A zero-length run must not create an empty node.
            Create((1, 3), (2, 0), (3, 2));
            Assert.AreEqual(5, _list.Length);
            AssertInvariants("AddInterval count 0");
            AssertNodes((1, 3), (3, 5));
        }

        [Test]
        public void Set_MergeRightAtIndexZero_LeavesNoEmptyNode(){
            // [A:1][B:3], Set(0, B) must give [B:3], not [A:0][B:3].
            Create((1, 1), (2, 2));
            Assert.IsTrue(_list.Set(0, 2));
            Assert.AreEqual(2, _list.Get(0));
            AssertInvariants("merge right at index 0");
            AssertNodes((2, 3));
        }

        #endregion

        #region Targeted Set branches

        [Test]
        public void Set_MergesBothSides(){
            // [X,A,X] -> [X,X,X]
            Create((1, 3), (2, 1), (1, 3));
            Assert.IsTrue(_list.Set(3, 1));
            AssertNodes((1, 7));
        }

        [Test]
        public void Set_MergesLeft(){
            // [X,A,A,Y] -> [X,X,A,Y]
            Create((1, 3), (2, 3));
            Assert.IsTrue(_list.Set(3, 1));
            AssertNodes((1, 4), (2, 6));
        }

        [Test]
        public void Set_MergesLeft_RemovesSingletonNode(){
            // [X,A,Y] -> [X,X,Y]
            Create((1, 3), (2, 1), (3, 3));
            Assert.IsTrue(_list.Set(3, 1));
            AssertNodes((1, 4), (3, 7));
        }

        [Test]
        public void Set_MergesRight(){
            // [X,A,A,Y] -> [X,A,Y,Y]
            Create((1, 3), (2, 3));
            Assert.IsTrue(_list.Set(2, 2));
            AssertNodes((1, 2), (2, 6));
        }

        [Test]
        public void Set_MergesRight_RemovesSingletonNode(){
            // [X,A,Y] -> [X,Y,Y]
            Create((1, 3), (2, 1), (3, 3));
            Assert.IsTrue(_list.Set(3, 3));
            AssertNodes((1, 3), (3, 7));
        }

        [Test]
        public void Set_SplitsInTheMiddle(){
            // [X,X,X] -> [X,A,X]
            Create((1, 5));
            Assert.IsTrue(_list.Set(2, 9));
            AssertNodes((1, 2), (9, 3), (1, 5));
            Assert.AreEqual(9, _list.Get(2));
            Assert.AreEqual(1, _list.Get(1));
            Assert.AreEqual(1, _list.Get(3));
        }

        [Test]
        public void Set_FirstIndex(){
            // [X,X,Y] (start) -> [A,X,Y]
            Create((1, 5), (2, 2));
            Assert.IsTrue(_list.Set(0, 9));
            AssertNodes((9, 1), (1, 5), (2, 7));
        }

        [Test]
        public void Set_LastIndex(){
            // [X,Y,Y] (end) -> [X,Y,A]
            Create((2, 2), (1, 5));
            Assert.IsTrue(_list.Set(6, 9));
            AssertNodes((2, 2), (1, 6), (9, 7));
        }

        [Test]
        public void Set_InsertsBeforeRunStart(){
            // [X,Y,Y] -> [X,A,Y]
            Create((1, 2), (2, 3));
            Assert.IsTrue(_list.Set(2, 9));
            AssertNodes((1, 2), (9, 3), (2, 5));
        }

        [Test]
        public void Set_InsertsAfterRunEnd(){
            // [X,X,Y] -> [X,A,Y]
            Create((1, 3), (2, 3));
            Assert.IsTrue(_list.Set(2, 9));
            AssertNodes((1, 2), (9, 3), (2, 6));
        }

        [Test]
        public void Set_ReplacesSingletonBetweenDifferentNeighbours(){
            // [X,Y,Z] -> [X,A,Z]
            Create((1, 2), (2, 1), (3, 2));
            Assert.IsTrue(_list.Set(2, 9));
            AssertNodes((1, 2), (9, 3), (3, 5));
        }

        [Test]
        public void Set_ReplacesSingletonBetweenEqualNeighbours(){
            // [X,Y,X] -> [X,A,X]
            Create((1, 2), (2, 1), (1, 2));
            Assert.IsTrue(_list.Set(2, 9));
            AssertNodes((1, 2), (9, 3), (1, 5));
        }

        [Test]
        public void Set_SameValue_ReturnsFalseAndChangesNothing(){
            Create((1, 3), (2, 3));
            Assert.IsFalse(_list.Set(1, 1));
            Assert.IsFalse(_list.Set(4, 2));
            AssertNodes((1, 3), (2, 6));
        }

        [Test]
        public void Set_SingleElementList_ReplacesValue(){
            Create((1, 1));
            Assert.IsTrue(_list.Set(0, 5));
            AssertNodes((5, 1));
        }

        #endregion

        #region Neighbours, Clear, Length, bounds

        [Test]
        public void TryLeftOf_TryRightOf_AtBothEnds(){
            Create((1, 2), (2, 2));

            Assert.IsFalse(_list.TryLeftOf(0, out _));
            Assert.IsTrue(_list.TryLeftOf(1, out ushort left1));
            Assert.AreEqual(1, left1);
            Assert.IsTrue(_list.TryLeftOf(2, out ushort left2));
            Assert.AreEqual(1, left2);
            Assert.IsTrue(_list.TryLeftOf(3, out ushort left3));
            Assert.AreEqual(2, left3);

            Assert.IsFalse(_list.TryRightOf(3, out _));
            Assert.IsTrue(_list.TryRightOf(0, out ushort right0));
            Assert.AreEqual(1, right0);
            Assert.IsTrue(_list.TryRightOf(1, out ushort right1));
            Assert.AreEqual(2, right1);
            Assert.IsTrue(_list.TryRightOf(2, out ushort right2));
            Assert.AreEqual(2, right2);
        }

        [Test]
        public void Clear_ResetsLengthAndNodes(){
            Create((1, 3), (2, 3));
            _list.Clear();
            Assert.AreEqual(0, _list.Length);
            Assert.AreEqual(0, _list.CompressedLength);
            _list.AddInterval(7, 4);
            Assert.AreEqual(4, _list.Length);
            Assert.AreEqual(7, _list.Get(3));
        }

        [Test]
        public void Length_IsSumOfIntervals(){
            Create((1, 3), (2, 4), (3, 5));
            Assert.AreEqual(12, _list.Length);
            Assert.AreEqual(3, _list.CompressedLength);
        }

        [Test]
        public void NodeIndex_ReturnsContainingNode(){
            Create((1, 3), (2, 4), (3, 5));
            Assert.AreEqual(0, _list.NodeIndex(0));
            Assert.AreEqual(0, _list.NodeIndex(2));
            Assert.AreEqual(1, _list.NodeIndex(3));
            Assert.AreEqual(1, _list.NodeIndex(6));
            Assert.AreEqual(2, _list.NodeIndex(7));
            Assert.AreEqual(2, _list.NodeIndex(11));
        }

        [Test]
        public void Get_OutOfRange_ThrowsInEditor(){
            Create((1, 3));
            Assert.Throws<IndexOutOfRangeException>(() => _list.Get(-1));
            Assert.Throws<IndexOutOfRangeException>(() => _list.Get(3));
        }

        [Test]
        public void Set_OutOfRange_ThrowsInEditor(){
            Create((1, 3));
            Assert.Throws<IndexOutOfRangeException>(() => _list.Set(-1, 2));
            Assert.Throws<IndexOutOfRangeException>(() => _list.Set(3, 2));
            AssertNodes((1, 3));
        }

        #endregion
    }
}
