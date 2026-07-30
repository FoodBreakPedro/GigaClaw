using GigaClaw.Core.Automation.Policy;

namespace GigaClaw.Core.Tests.Automation;

/// <summary>
/// U17's host-side preflight. The lane spec is explicit that "an agent-mutable label alone is
/// insufficient", so these tests pin the properties that make the anchor trustworthy rather than
/// just the happy path.
/// </summary>
public class OutboundApprovalGateTests
{
    [Fact]
    public void A_fresh_install_sends_nothing()
    {
        // Dry-run by default is the whole point: an empty list is a deny, not an "allow all".
        var gate = new OutboundApprovalGate(() => []);

        var decision = gate.Evaluate("https://cms.example.com/api/draft");

        Assert.False(decision.MaySend);
        Assert.Contains("dry-run", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_approved_host_sends()
    {
        var gate = new OutboundApprovalGate(() => ["cms.example.com"]);

        Assert.True(gate.Evaluate("https://cms.example.com/api/draft").MaySend);
    }

    [Theory]
    [InlineData("https://notcms.example.com/api")]
    [InlineData("https://cms.example.com.evil.test/api")]
    [InlineData("https://evil.test/?target=cms.example.com")]
    public void Approval_does_not_leak_to_lookalike_hosts(string url)
    {
        // Suffix or substring matching would let one approval cover hosts the owner never saw.
        var gate = new OutboundApprovalGate(() => ["cms.example.com"]);

        Assert.False(gate.Evaluate(url).MaySend);
    }

    [Fact]
    public void Host_matching_ignores_case_but_not_identity()
    {
        var gate = new OutboundApprovalGate(() => ["CMS.Example.com"]);

        Assert.True(gate.Evaluate("https://cms.example.com/api").MaySend);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    public void An_unusable_url_is_a_dry_run_rather_than_an_exception(string? url)
    {
        var gate = new OutboundApprovalGate(() => ["cms.example.com"]);

        var decision = gate.Evaluate(url);

        Assert.False(decision.MaySend);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void An_unsubstituted_placeholder_never_sends()
    {
        // A template that failed to render must not be able to match an approved host by accident.
        var gate = new OutboundApprovalGate(() => ["cms.example.com"]);

        var decision = gate.Evaluate("https://cms.example.com/api/{ticketId}");

        Assert.False(decision.MaySend);
        Assert.Contains("placeholder", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://cms.example.com/x")]
    public void Only_http_and_https_are_eligible(string url)
    {
        var gate = new OutboundApprovalGate(() => ["cms.example.com", "etc"]);

        Assert.False(gate.Evaluate(url).MaySend);
    }

    [Fact]
    public void The_list_is_read_per_call_so_an_owner_edit_takes_effect_without_a_restart()
    {
        var approved = new List<string>();
        var gate = new OutboundApprovalGate(() => approved);

        Assert.False(gate.Evaluate("https://cms.example.com/api").MaySend);

        approved.Add("cms.example.com");

        Assert.True(gate.Evaluate("https://cms.example.com/api").MaySend);
    }

    [Fact]
    public void The_reason_names_the_host_so_the_owner_knows_what_to_approve()
    {
        var gate = new OutboundApprovalGate(() => ["other.example.com"]);

        var decision = gate.Evaluate("https://cms.example.com/api/draft");

        Assert.Contains("cms.example.com", decision.Reason!, StringComparison.Ordinal);
    }
}
