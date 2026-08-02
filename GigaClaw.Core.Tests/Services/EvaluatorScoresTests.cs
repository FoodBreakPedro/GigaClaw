using GigaClaw.Core.Services;
using GigaClaw.Core.Tests.Helpers;

namespace GigaClaw.Core.Tests.Services;

/// <summary>
/// Plan 4.2 §8: the first consumer of the evaluator's <c>scores.json</c>. Both documented shapes
/// (the score cache from <c>references/procedure-steps.md</c> and the typed verdict-contract v1
/// record from <c>SKILL.md</c>) must aggregate, and a malformed file must degrade to an empty tile.
/// </summary>
public sealed class EvaluatorScoresTests
{
    [Fact]
    public void Parse_ReadsTheScoreCacheShape_GroupedByWorker()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "revision": 12,
              "tickets": {
                "388": {
                  "worker": "programmer",
                  "firstPass": true,
                  "feedbackCompliance": 1.0,
                  "deliveryQuality": 0.5,
                  "blocked": false,
                  "source": { "updatedAt": "2026-04-19T15:00:00Z", "commentCount": 4, "activityCount": 9 },
                  "evaluatedAt": "2026-04-19T15:01:00Z"
                },
                "389": {
                  "worker": "programmer",
                  "firstPass": false,
                  "feedbackCompliance": 0.5,
                  "deliveryQuality": 1.0,
                  "blocked": true,
                  "evaluatedAt": "2026-04-20T15:01:00Z"
                },
                "390": {
                  "worker": "blog-writer",
                  "firstPass": true,
                  "deliveryQuality": 1.0,
                  "blocked": false,
                  "evaluatedAt": "2026-04-21T09:00:00Z"
                }
              },
              "retryQueue": { "391": { "attempts": 2, "state": "pending", "lastError": "HTTP 500" } },
              "pendingWorkers": ["programmer"]
            }
            """;

        var scores = EvaluatorScores.Parse(json);

        Assert.Equal(2, scores.Count);
        var programmer = scores.Single(s => s.Agent == "programmer");
        Assert.Equal(2, programmer.Samples);
        Assert.Equal(0.5, programmer.FirstPassRate);
        Assert.Equal(0.75, programmer.DeliveryQuality);
        Assert.Equal(0.75, programmer.FeedbackCompliance);
        Assert.Equal(0.5, programmer.BlockRate);
        Assert.Equal(new DateTime(2026, 4, 20, 15, 1, 0, DateTimeKind.Utc), programmer.LastEvaluatedAtUtc);
        // The retry queue holds failures, not scores — a queued ticket id must never become an agent.
        Assert.DoesNotContain(scores, s => s.Agent == "391");
    }

    [Fact]
    public void Parse_ReadsTheVerdictContractShape_AndNormalizesEveryCategoryOntoZeroToFive()
    {
        const string json = """
            [
              {
                "schemaVersion": 1,
                "agent": "evaluator",
                "worker": "programmer",
                "ticketId": 388,
                "verdict": "SHIP",
                "categories": [
                  { "name": "Outcome quality", "score": 4, "max": 5 },
                  { "name": "Process discipline", "score": 5, "max": 5 },
                  { "name": "Efficiency", "score": 3, "max": 5 },
                  { "name": "Communication", "score": 8, "max": 10 }
                ],
                "reviewedAtUtc": "2026-07-30T02:05:00Z"
              }
            ]
            """;

        var score = Assert.Single(EvaluatorScores.Parse(json));

        Assert.Equal("programmer", score.Agent);
        Assert.Equal(1, score.Samples);
        Assert.Equal(4, score.Outcome);
        Assert.Equal(5, score.Process);
        Assert.Equal(3, score.Efficiency);
        Assert.Equal(4, score.Communication); // 8/10 rescaled onto the same 0–5 axis
    }

    [Fact]
    public void Parse_FallsBackToTheAuthorWhenNoWorkerIsNamed()
    {
        const string json = """
            { "agent": "evaluator", "categories": [ { "name": "Outcome", "score": 2, "max": 5 } ] }
            """;

        var score = Assert.Single(EvaluatorScores.Parse(json));
        Assert.Equal("evaluator", score.Agent);
        Assert.Equal(2, score.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"schemaVersion\": 1, \"tickets\": {} }")]
    public void Parse_DegradesToEmptyRatherThanThrowing(string json)
    {
        Assert.Empty(EvaluatorScores.Parse(json));
    }

    [Fact]
    public void ReadForWorkspace_ReturnsEmptyWhenTheFileIsAbsent()
    {
        using var tmp = new TempDir();
        Assert.Empty(EvaluatorScores.ReadForWorkspace(tmp.Path));
    }

    [Fact]
    public void ReadForWorkspace_ReadsTheEvaluatorMemoryPath()
    {
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, ".agents", "evaluator", "memory");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "scores.json"),
            """{ "tickets": { "1": { "worker": "qa-tester", "firstPass": true, "deliveryQuality": 1.0 } } }""");

        var score = Assert.Single(EvaluatorScores.ReadForWorkspace(tmp.Path));
        Assert.Equal("qa-tester", score.Agent);
        Assert.Equal(1.0, score.DeliveryQuality);
        Assert.Null(score.Outcome); // the cache shape has no categories — absent, not zero
    }
}
