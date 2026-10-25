using Engine.Editor.WorldGen;
using NUnit.Framework;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>The species gallery of the preview: one row per variant, views sized to the reach, the narrow option.</summary>
    public class TreeGalleryTests{
        private FloraFixture _fixture;
        private TreeGallery _gallery;

        [SetUp]
        public void SetUp(){
            _fixture = new FloraFixture();
            _gallery = new TreeGallery();
        }

        [TearDown]
        public void TearDown(){
            _gallery.Dispose();
            _fixture.Dispose();
        }

        [Test]
        public void Build_DrawsEveryVariantForTheRowOfSeeds(){
            const int count = 4;
            _gallery.Build(_fixture.Grove, 7, count, false);
            Assert.AreEqual(_fixture.Grove.Variants.Count, _gallery.Rows.Count);
            foreach (TreeGallery.Row row in _gallery.Rows){
                int cell = 2 * row.Def.Reach + 1;
                Assert.AreEqual(count * (cell + 1) + 1, row.Side.width, row.Name);
                Assert.AreEqual(row.Side.width, row.Top.width, row.Name);
                Assert.AreEqual(cell + 2, row.Top.height, row.Name);
                Assert.AreEqual(row.Height.y + 2, row.Side.height, $"{row.Name}: ground row + tallest tree + gap");
                Assert.Greater(row.VoxelCount.x, 0, row.Name);
                Assert.AreEqual(0, row.Narrowed, row.Name);

                // The trunk of the first tree stands on the ground in the side view.
                Color32 trunk = row.Side.GetPixel(1 + row.Def.Reach, 1);
                Color32 background = row.Side.GetPixel(0, row.Side.height - 1);
                Assert.AreNotEqual(background, trunk, $"{row.Name}: trunk voxel above the ground row");
            }

            float share = 0f;
            foreach (TreeGallery.Row row in _gallery.Rows) share += row.Share;
            Assert.AreEqual(1f, share, 1e-5f);
        }

        [Test]
        public void Build_Narrow_BuildsWideAndLeaningTrunksUpright(){
            _gallery.Build(_fixture.Grove, 7, 8, true);
            int wide = _fixture.Grove.Variants.FindIndex(v => v.TrunkWidth == 2);
            Assert.AreEqual(8, _gallery.Rows[wide].Narrowed, "every width-2 trunk is narrowed");
            Assert.AreEqual(0, _gallery.Rows[0].Narrowed, "Normal: 1 wide, upright");
        }

        [Test]
        public void Build_SpeciesWithoutVariants_HasNoRows(){
            _gallery.Build(_fixture.MakeTree("Empty"), 1, 4, false);
            Assert.AreEqual(0, _gallery.Rows.Count);
        }
    }
}
