using TabularOps.Core.Tracing;

namespace TabularOps.Core.Tests;

public class TraceEventTests
{
    [Fact]
    public void Summary_WithNoSubclass_ReturnsEventClass()
    {
        var evt = new TraceEvent { EventClass = "Progress Report Begin" };

        Assert.Equal("Progress Report Begin", evt.Summary);
    }

    [Fact]
    public void Summary_WithSubclass_ReturnsCombined()
    {
        var evt = new TraceEvent
        {
            EventClass    = "Progress Report Begin",
            EventSubclass = "VertiPaq Query",
        };

        Assert.Equal("Progress Report Begin / VertiPaq Query", evt.Summary);
    }

    [Fact]
    public void Summary_WithEmptySubclass_ReturnsEventClassOnly()
    {
        var evt = new TraceEvent { EventClass = "Error", EventSubclass = "" };

        Assert.Equal("Error", evt.Summary);
    }

    [Fact]
    public void Summary_WithNullSubclass_ReturnsEventClassOnly()
    {
        var evt = new TraceEvent { EventClass = "Query End", EventSubclass = null };

        Assert.Equal("Query End", evt.Summary);
    }

    [Fact]
    public void TraceEvent_IsImmutableRecord_WithEquality()
    {
        var a = new TraceEvent { Id = 1, EventClass = "Progress Report Begin", DurationMs = 123 };
        var b = new TraceEvent { Id = 1, EventClass = "Progress Report Begin", DurationMs = 123 };

        Assert.Equal(a, b);
    }

    [Fact]
    public void TraceEvent_WithExpression_ProducesIndependentCopy()
    {
        var original = new TraceEvent { EventClass = "Error", Text = "original" };
        var copy = original with { Text = "modified" };

        Assert.Equal("original", original.Text);
        Assert.Equal("modified", copy.Text);
    }
}
