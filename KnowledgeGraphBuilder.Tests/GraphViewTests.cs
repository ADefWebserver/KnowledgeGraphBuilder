using System.Diagnostics;
using KnowledgeGraphBuilder.Shared.Knowledge;

namespace KnowledgeGraphBuilder.Tests;

public class GraphViewTests
{
    static async Task<GraphSnapshot> SnapshotAsync(Dictionary<string, string> pages)
    {
        var graph = new KnowledgeGraphService(new GraphAndWorkspaceTests.MemoryStore(pages));
        await graph.RebuildAsync();
        return graph.Snapshot;
    }

    static Dictionary<string, string> Sample() => new()
    {
        ["Welcome.md"] = "# Welcome\n\nSee [[Install]] and [[Ideas]].",
        ["Docs/Install.md"] = "# Install\n\nBack to [[Welcome]]. See [[Linking]] and [[Install]].",
        ["Docs/Linking.md"] = "# Linking\n\nSee [[Install#Steps]] and [[Roadmap|the plan]].",
        ["Lonely.md"] = "# Lonely\n\nNo links.",
    };

    [Fact]
    public async Task WholeGraph_HasOneEdgePerConnectedPair_AndMissingTargets()
    {
        var view = GraphViewBuilder.Build(await SnapshotAsync(Sample()), new GraphViewOptions());

        Assert.Equal(["Docs/Install.md", "Docs/Linking.md", "Lonely.md", "Welcome.md"], view.Nodes.Where(n => !n.Missing).Select(n => n.Path));
        Assert.Equal(["Ideas", "Roadmap"], view.Nodes.Where(n => n.Missing).Select(n => n.Label).Order());

        // Welcome<->Install both ways is one edge; Install's link to itself is dropped.
        var pageEdges = view.Edges.Where(e => !e.ToMissing).Select(e => $"{e.From}>{e.To}").ToList();
        Assert.Equal(["Docs/Install.md>Docs/Linking.md", "Docs/Install.md>Welcome.md"], pageEdges);
        Assert.Equal(2, view.Edges.Count(e => e.ToMissing));
        Assert.False(view.Truncated);
        Assert.Equal(4, view.TotalPages);

        var install = view.Nodes.Single(n => n.Path == "Docs/Install.md");
        Assert.Equal(2, install.Degree);
        Assert.Equal("Docs", install.Group);
        Assert.Equal("Install", install.Label);
    }

    [Fact]
    public async Task MissingPages_CanBeHidden()
    {
        var view = GraphViewBuilder.Build(await SnapshotAsync(Sample()), new GraphViewOptions(IncludeMissing: false));
        Assert.DoesNotContain(view.Nodes, n => n.Missing);
        Assert.DoesNotContain(view.Edges, e => e.ToMissing);
    }

    [Fact]
    public async Task AroundPage_FollowsLinksBothWays_UpToTheDepth()
    {
        var snapshot = await SnapshotAsync(Sample());

        var one = GraphViewBuilder.Build(snapshot, new GraphViewOptions("Welcome.md", Depth: 1, IncludeMissing: false));
        Assert.Equal(["Docs/Install.md", "Welcome.md"], one.Nodes.Select(n => n.Path));

        var two = GraphViewBuilder.Build(snapshot, new GraphViewOptions("welcome.md", Depth: 2, IncludeMissing: false));
        Assert.Equal(["Docs/Install.md", "Docs/Linking.md", "Welcome.md"], two.Nodes.Select(n => n.Path));

        // The center page sits in the middle of the picture.
        var center = two.Nodes.Single(n => n.Path == "Welcome.md");
        Assert.Equal((0.0, 0.0), (center.X, center.Y));
    }

    [Fact]
    public async Task AnUnknownCenter_ShowsTheWholeGraph()
    {
        var view = GraphViewBuilder.Build(await SnapshotAsync(Sample()), new GraphViewOptions("Nope.md", IncludeMissing: false));
        Assert.Equal(4, view.Nodes.Count);
    }

    [Fact]
    public async Task BigGraphs_KeepTheBestConnectedPages()
    {
        var pages = new Dictionary<string, string> { ["Hub.md"] = "# Hub\n" + string.Join(" ", Enumerable.Range(0, 30).Select(i => $"[[P{i}]]")) };
        for (var i = 0; i < 30; i++) pages[$"P{i}.md"] = $"# P{i}\n";
        for (var i = 0; i < 10; i++) pages[$"Alone{i}.md"] = $"# Alone{i}\n";

        var view = GraphViewBuilder.Build(await SnapshotAsync(pages), new GraphViewOptions(MaxNodes: 31));

        Assert.True(view.Truncated);
        Assert.Equal(41, view.TotalPages);
        Assert.Equal(31, view.Nodes.Count);
        Assert.Contains(view.Nodes, n => n.Path == "Hub.md");
        Assert.DoesNotContain(view.Nodes, n => n.Path!.StartsWith("Alone"));
    }

    [Theory]
    [InlineData("Install#Steps", "Install")]
    [InlineData("Roadmap|the plan", "Roadmap")]
    [InlineData("notes/Idea.md", "notes/Idea")]
    [InlineData("./My%20Page.md#top", "My Page")]
    [InlineData("#only-a-heading", null)]
    [InlineData("  ", null)]
    public void MissingName_StripsHeadingsAliasesAndExtensions(string raw, string? expected) =>
        Assert.Equal(expected, GraphViewBuilder.MissingName(raw));

    [Fact]
    public async Task Layout_IsDeterministic_AndSpreadsNodesOut()
    {
        var snapshot = await SnapshotAsync(Sample());
        var a = GraphViewBuilder.Build(snapshot, new GraphViewOptions());
        var b = GraphViewBuilder.Build(snapshot, new GraphViewOptions());

        Assert.Equal(a.Nodes.Select(n => (n.X, n.Y)), b.Nodes.Select(n => (n.X, n.Y)));
        Assert.All(a.Nodes, n => Assert.True(double.IsFinite(n.X) && double.IsFinite(n.Y)));
        foreach (var n in a.Nodes)
            foreach (var m in a.Nodes.Where(m => m != n))
                Assert.True(Math.Abs(n.X - m.X) + Math.Abs(n.Y - m.Y) > 10, $"{n.Id} and {m.Id} overlap");
    }

    [Fact]
    public async Task Layout_OfFourHundredNodes_IsQuick()
    {
        var pages = new Dictionary<string, string>();
        for (var i = 0; i < 400; i++) pages[$"F{i % 8}/P{i}.md"] = $"# P{i}\n[[P{(i + 1) % 400}]] [[P{(i * 7) % 400}]]";
        var snapshot = await SnapshotAsync(pages);

        var sw = Stopwatch.StartNew();
        var view = GraphViewBuilder.Build(snapshot, new GraphViewOptions());
        sw.Stop();

        Assert.Equal(400, view.Nodes.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Layout took {sw.ElapsedMilliseconds} ms");
        var (_, _, width, height) = view.Bounds();
        Assert.InRange(width, 300, 20000);
        Assert.InRange(height, 300, 20000);
    }
}
