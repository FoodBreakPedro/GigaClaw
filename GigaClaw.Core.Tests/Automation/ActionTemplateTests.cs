namespace GigaClaw.Core.Tests.Automation;

public class ActionTemplateTests
{
    [Fact]
    public void RenderCommentTemplate_substitutes_all_placeholders()
    {
        var result = ConditionEvaluators.RenderCommentTemplate(
            "Ticket #{ticketId} [{ticketTitle}] assigned to {assignee}",
            42, "Fix drawer", "programmer");
        Assert.Equal("Ticket #42 [Fix drawer] assigned to programmer", result);
    }

    [Fact]
    public void RenderCommentTemplate_replaces_missing_values_with_empty()
    {
        var result = ConditionEvaluators.RenderCommentTemplate(
            "ID:{ticketId} T:{ticketTitle} A:{assignee}", null, null, null);
        Assert.Equal("ID: T: A:", result);
    }

    [Fact]
    public void RenderCommentTemplate_leaves_other_braces_alone()
    {
        var result = ConditionEvaluators.RenderCommentTemplate(
            "{other} {ticketId}", 1, null, null);
        Assert.Equal("{other} 1", result);
    }

    // ── Shared render helper ────────────────────────────────────────────────

    [Fact]
    public void Render_substitutes_supplied_placeholders()
    {
        var result = ActionTemplate.Render("a={a} b={b}", new Dictionary<string, string?>
        {
            ["a"] = "1",
            ["b"] = "2",
        });
        Assert.Equal("a=1 b=2", result);
    }

    [Fact]
    public void Render_leaves_unknown_placeholders_untouched()
    {
        var result = ActionTemplate.Render("{known} {unknown}", new Dictionary<string, string?>
        {
            ["known"] = "yes",
        });
        Assert.Equal("yes {unknown}", result);
    }

    [Fact]
    public void Render_treats_null_values_as_empty()
    {
        var result = ActionTemplate.Render("[{a}]", new Dictionary<string, string?> { ["a"] = null });
        Assert.Equal("[]", result);
    }

    [Fact]
    public void Render_of_null_or_empty_template_is_empty()
    {
        var values = new Dictionary<string, string?> { ["a"] = "1" };
        Assert.Equal("", ActionTemplate.Render(null, values));
        Assert.Equal("", ActionTemplate.Render("", values));
    }

    [Fact]
    public void Render_replaces_every_occurrence_of_a_placeholder()
    {
        var result = ActionTemplate.Render("{a}-{a}", new Dictionary<string, string?> { ["a"] = "x" });
        Assert.Equal("x-x", result);
    }

    // ── Chain values ────────────────────────────────────────────────────────

    [Fact]
    public void Render_substitutes_chain_values_captured_by_earlier_actions()
    {
        var state = new ActionState();
        state.ChainValues["http.status"] = "201";
        state.ChainValues["http.body.adminUrl"] = "https://cms.example/admin/42";

        var result = ActionTemplate.Render(
            "Ticket #{ticketId} → {http.body.adminUrl} ({http.status})",
            state,
            new TriggerFiring(7, "Launch", "Review"));

        Assert.Equal("Ticket #7 → https://cms.example/admin/42 (201)", result);
    }

    [Fact]
    public void Render_leaves_unpublished_chain_values_untouched()
    {
        var state = new ActionState();
        state.ChainValues["http.status"] = "200";

        var result = ActionTemplate.Render("{http.status}/{http.body.missing}", state,
            new TriggerFiring(1, "t", "Review"));

        Assert.Equal("200/{http.body.missing}", result);
    }

    [Fact]
    public void Render_without_state_still_resolves_the_firing_placeholders()
    {
        var result = ActionTemplate.Render("#{ticketId} {ticketTitle}", null,
            new TriggerFiring(3, "Title", "Todo"));
        Assert.Equal("#3 Title", result);
    }

    [Fact]
    public void Values_lets_caller_placeholders_win_over_chain_values()
    {
        var state = new ActionState();
        state.ChainValues["slug"] = "from-chain";

        var result = ActionTemplate.Render("{slug}", ActionTemplate.Values(state, ("slug", "explicit")));
        Assert.Equal("explicit", result);
    }

    [Fact]
    public void Values_exposes_chain_values_alongside_caller_placeholders()
    {
        var state = new ActionState();
        state.ChainValues["http.body.id"] = "42";

        var result = ActionTemplate.Render(
            "{slug}:{http.body.id}", ActionTemplate.Values(state, ("slug", "proj")));
        Assert.Equal("proj:42", result);
    }
}
