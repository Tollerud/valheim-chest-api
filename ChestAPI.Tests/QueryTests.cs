using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Xunit;

namespace ChestAPI.Tests
{
    public class QueryTests
    {
        // Shaped like real server data: one player's chest in the Meadows, dungeon/world chests with no owner
        // (high Y = underground crypt/cave), and a second player's chest further away.
        private static List<ChestDto> World()
        {
            return new List<ChestDto>
            {
                Chest("1:395697", "piece_chest_wood", 54, 35, -2395, "Meadows", owner: 297184578, loaded: false,
                      Item("Resin", 50), Item("Resin", 50), Item("Resin", 45)),
                Chest("1:68084", "TreasureChest_blackforest", -336, 70, 771, "BlackForest", owner: 0, loaded: false),
                Chest("1:160391", "TreasureChest_blackforest", 205, 49, -1599, "BlackForest", owner: 0, loaded: false,
                      Item("Coins", 9), Item("ArrowFlint", 6)),
                Chest("1:441188", "TreasureChest_forestcrypt", 1800, 5050, -1517, "BlackForest", owner: 0, loaded: false,
                      Item("Coins", 12), Item("Ruby", 2)),
                Chest("1:5", "piece_chest", 100, 30, -2300, "Meadows", owner: 111, loaded: true,
                      Item("Resin", 10), Item("Iron", 200), Item("Chain", 4)),
                Chest("1:6", "piece_chest_private", 3000, 30, 3000, "Mistlands", owner: 111, loaded: true,
                      Item("Chain", 5)),
            };
        }

        private static ChestDto Chest(string id, string prefab, float x, float y, float z, string biome, long owner, bool loaded, params ItemDto[] items)
        {
            return new ChestDto
            {
                Id = id,
                PrefabName = prefab,
                Position = new Vec3Dto { X = x, Y = y, Z = z },
                Biome = biome,
                Owner = owner == 0 ? null : new OwnerDto { PlayerId = owner },
                Loaded = loaded,
                LastUpdatedUtc = new DateTime(2026, 10, 20, 22, 0, 0, DateTimeKind.Utc).AddMinutes(loaded ? 0 : -30),
                Items = items.ToList(),
            };
        }

        private static ItemDto Item(string prefab, int stack) => new ItemDto { PrefabName = prefab, DisplayName = prefab, Stack = stack };

        private static PlayerNames Names()
        {
            var n = new PlayerNames(null);
            n.Learn(297184578, "Mathias");
            n.Learn(111, "Ola");
            return n;
        }

        private static ChestFilter Parse(string query, bool playerOnly, out string error, Dictionary<string, Area> areas = null, params string[] extra)
        {
            var q = new NameValueCollection();
            foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                q.Add(kv[0], kv[1]);
            }

            return ChestFilter.Parse(q, playerOnly, areas, Names(), extra, out error);
        }

        private static List<string> Ids(ChestFilter f) => f.Apply(World()).Select(c => c.Id).OrderBy(x => x).ToList();

        [Fact]
        public void PlayerPlacedMeansHasCreator()
        {
            var w = World();
            Assert.True(w.First(c => c.Id == "1:395697").PlayerPlaced);
            Assert.False(w.First(c => c.Id == "1:68084").PlayerPlaced);
        }

        [Fact]
        public void PlayerOnlyDropsDungeonAndWorldChests()
        {
            var f = Parse("", playerOnly: true, out var err);
            Assert.Null(err);
            Assert.Equal(new[] { "1:395697", "1:5", "1:6" }, Ids(f));
        }

        [Fact]
        public void WithoutPlayerOnlyEverythingMatches()
        {
            Assert.Equal(6, Ids(Parse("", false, out _)).Count);
        }

        [Fact]
        public void OwnerByIdAndByNameBothWork()
        {
            Assert.Equal(new[] { "1:5", "1:6" }, Ids(Parse("owner=111", true, out _)));
            Assert.Equal(new[] { "1:5", "1:6" }, Ids(Parse("owner=ola", true, out _)));
            Assert.Equal(new[] { "1:395697" }, Ids(Parse("owner=Mathias", true, out _)));
            Assert.Equal(new[] { "1:395697", "1:5", "1:6" }, Ids(Parse("owner=Mathias,111", true, out _)));
        }

        [Fact]
        public void UnknownOwnerNameIsAnError()
        {
            Assert.Null(Parse("owner=Nobody", true, out var err));
            Assert.Contains("Unknown player 'Nobody'", err);
        }

        [Fact]
        public void BoundingBoxUsesXAndZ()
        {
            var f = Parse("minX=0&maxX=500&minZ=-2500&maxZ=-2000", true, out _);
            Assert.Equal(new[] { "1:395697", "1:5" }, Ids(f));
        }

        [Fact]
        public void BoundingBoxEdgesAreInclusiveAndOpenEnded()
        {
            Assert.Equal(new[] { "1:395697", "1:5" }, Ids(Parse("minX=54&maxX=100", true, out _)));
            Assert.Equal(new[] { "1:6" }, Ids(Parse("minX=1000", true, out _)));
        }

        [Fact]
        public void CircleUsesDistanceInXZNotY()
        {
            var f = Parse("x=80&z=-2350&radius=60", true, out _);
            Assert.Equal(new[] { "1:395697", "1:5" }, Ids(f));
            Assert.Equal(new[] { "1:5" }, Ids(Parse("x=100&z=-2300&radius=1", true, out _)));
        }

        [Fact]
        public void CircleNeedsAllThreeParts()
        {
            Assert.Null(Parse("x=1&z=2", true, out var err));
            Assert.Contains("together", err);
            Assert.Null(Parse("x=1&z=2&radius=0", true, out err));
        }

        [Fact]
        public void HeightFilterSeparatesUndergroundFromSurface()
        {
            Assert.Equal(new[] { "1:441188" }, Ids(Parse("minY=1000", false, out _)));
            Assert.DoesNotContain("1:441188", Ids(Parse("maxY=1000", false, out _)));
        }

        [Fact]
        public void NamedAreasAreUnionedAndCombineWithExplicitBox()
        {
            var areas = AreaParser.Parse("base:circle:80,-2350,60;far:rect:2900,2900,3100,3100", out var problems);
            Assert.Empty(problems);

            Assert.Equal(new[] { "1:395697", "1:5" }, Ids(Parse("area=base", true, out _, areas)));
            Assert.Equal(new[] { "1:395697", "1:5", "1:6" }, Ids(Parse("area=base,far", true, out _, areas)));
            Assert.Equal(new[] { "1:5" }, Ids(Parse("area=base&minX=90", true, out _, areas)));
            Assert.Null(Parse("area=nope", true, out var err, areas));
            Assert.Contains("Unknown area", err);
        }

        [Fact]
        public void AreaParserReportsBadEntriesAndKeepsGoodOnes()
        {
            var areas = AreaParser.Parse("ok:rect:5,5,-5,-5; bad ; c:circle:0,0,0 ; d:rect:1,2,x,4", out var problems);
            Assert.Single(areas);
            Assert.Equal(-5, areas["ok"].MinX);
            Assert.Equal(5, areas["ok"].MaxX);
            Assert.Equal(3, problems.Count);
        }

        [Fact]
        public void PrefabBiomeLoadedAndItemFilters()
        {
            Assert.Equal(new[] { "1:395697" }, Ids(Parse("prefab=piece_chest_wood", true, out _)));
            Assert.Equal(new[] { "1:395697", "1:6" }.Concat(new[] { "1:5" }).OrderBy(x => x), Ids(Parse("excludePrefab=nothing", true, out _)));
            Assert.Equal(new[] { "1:395697", "1:5" }, Ids(Parse("biome=Meadows", true, out _)));
            Assert.Equal(new[] { "1:5", "1:6" }, Ids(Parse("loaded=true", true, out _)));
            Assert.Equal(new[] { "1:5", "1:6" }, Ids(Parse("item=Chain", true, out _)));
        }

        [Fact]
        public void UnknownAndMalformedParametersAreRejected()
        {
            Assert.Null(Parse("minx5=1", true, out var err));
            Assert.Contains("Unknown parameter", err);
            Assert.Null(Parse("minX=abc", true, out err));
            Assert.Null(Parse("minX=10&maxX=5", true, out err));
            Assert.Null(Parse("loaded=maybe", true, out err));
            Assert.NotNull(Parse("need=Iron:5", true, out err, null, "need"));
        }

        [Fact]
        public void ParameterNamesAreCaseInsensitive()
        {
            var q = new NameValueCollection { { "MINX", "1000" } };
            var f = ChestFilter.Parse(q, true, null, Names(), null, out var err);
            Assert.Null(err);
            Assert.Equal(new[] { "1:6" }, f.Apply(World()).Select(c => c.Id).ToList());
        }

        [Fact]
        public void TotalsCountStacksAndChestsOncePerChest()
        {
            var chests = Parse("", true, out _).Apply(World());
            var resin = ChestAggregates.Totals(chests, null).First(t => t.PrefabName == "Resin");
            Assert.Equal(155, resin.TotalStack);
            Assert.Equal(2, resin.ChestCount);

            var chain = ChestAggregates.Totals(chests, new HashSet<string>(new[] { "chain" }, StringComparer.OrdinalIgnoreCase));
            Assert.Single(chain);
            Assert.Equal(9, chain[0].TotalStack);
            Assert.Equal(2, chain[0].ChestCount);
        }

        [Fact]
        public void PlayerScopedTotalsExcludeDungeonLoot()
        {
            var all = ChestAggregates.Totals(Parse("", false, out _).Apply(World()), null);
            var mine = ChestAggregates.Totals(Parse("", true, out _).Apply(World()), null);
            Assert.Contains(all, t => t.PrefabName == "Coins");
            Assert.DoesNotContain(mine, t => t.PrefabName == "Coins");
        }

        [Fact]
        public void CheckReportsHaveMissingAndWhere()
        {
            var chests = Parse("", true, out _).Apply(World());
            var needs = ChestAggregates.ParseNeeds(new[] { "Iron:150,Chain=20", "Resin" , "Flax:3"}, out var err);
            Assert.Null(err);

            var result = ChestAggregates.Check(chests, needs, includeWhere: true);
            Assert.False(result.AllSatisfied);

            var iron = result.Items.Single(i => i.PrefabName == "Iron");
            Assert.Equal(200, iron.Have);
            Assert.Equal(0, iron.Missing);

            var chain = result.Items.Single(i => i.PrefabName == "Chain");
            Assert.Equal(9, chain.Have);
            Assert.Equal(11, chain.Missing);
            Assert.Equal(new[] { 5, 4 }, chain.Where.Select(w => w.Stack).ToArray());

            var flax = result.Items.Single(i => i.PrefabName == "Flax");
            Assert.Equal(3, flax.Missing);
            Assert.Empty(flax.Where);

            Assert.Equal(1, result.Items.Single(i => i.PrefabName == "Resin").Needed);
        }

        [Fact]
        public void CheckWithoutWhereOmitsLocations()
        {
            var needs = ChestAggregates.ParseNeeds(new[] { "Iron:1" }, out _);
            Assert.Null(ChestAggregates.Check(World(), needs, false).Items[0].Where);
            Assert.True(ChestAggregates.Check(World(), needs, false).AllSatisfied);
        }

        [Fact]
        public void NeedsAreMergedAndValidated()
        {
            var merged = ChestAggregates.ParseNeeds(new[] { "Iron:5,iron:7" }, out _);
            Assert.Single(merged);
            Assert.Equal(12, merged[0].Value);

            Assert.Null(ChestAggregates.ParseNeeds(new[] { "Iron:0" }, out var err));
            Assert.Null(ChestAggregates.ParseNeeds(new[] { "Iron:x" }, out err));
            Assert.Null(ChestAggregates.ParseNeeds(null, out err));
            Assert.Contains("at least one", err);
        }

        [Fact]
        public void MetaTellsHowFreshTheNumbersAre()
        {
            var meta = ChestAggregates.Meta(World());
            Assert.Equal(6, meta.ChestCount);
            Assert.Equal(2, meta.LiveChests);
            Assert.Equal(4, meta.ScannedChests);
            Assert.Equal(new DateTime(2026, 10, 20, 21, 30, 0, DateTimeKind.Utc), meta.OldestUpdateUtc);
            Assert.Null(ChestAggregates.Meta(new List<ChestDto>()).OldestUpdateUtc);
        }

        [Fact]
        public void PlayersGroupsByOwnerAndUsesTheNameCache()
        {
            var players = ChestAggregates.Players(World(), Names());
            Assert.Equal(2, players.Count);
            Assert.Equal(111, players[0].PlayerId);
            Assert.Equal("Ola", players[0].PlayerName);
            Assert.Equal(2, players[0].ChestCount);
        }

        [Fact]
        public void WithoutItemsKeepsTheRestOfTheChest()
        {
            var c = World()[0];
            var copy = c.WithoutItems();
            Assert.Empty(copy.Items);
            Assert.Equal(3, c.Items.Count);
            Assert.Equal(c.Id, copy.Id);
            Assert.Equal(c.Position, copy.Position);
        }

        [Fact]
        public void PlayerNamesPersistAndIgnoreJunk()
        {
            var path = Path.Combine(Path.GetTempPath(), "chestapi-names-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var a = new PlayerNames(path);
                Assert.True(a.Learn(42, "Bjorn"));
                Assert.False(a.Learn(42, "Bjorn"));
                Assert.False(a.Learn(0, "Nobody"));
                Assert.False(a.Learn(7, ""));
                a.SaveIfDirty();

                var b = new PlayerNames(path);
                b.Load();
                Assert.Equal("Bjorn", b.GetName(42));
                Assert.Equal(1, b.Count);
                Assert.Equal(new List<long> { 42 }, b.FindIds("bjorn"));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
