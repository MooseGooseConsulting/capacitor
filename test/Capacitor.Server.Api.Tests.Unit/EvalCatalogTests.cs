namespace Capacitor.Server.Api.Tests.Unit;

public class EvalCatalogTests {
    [Test]
    public async Task GetCatalog_returns_complete_taxonomy_across_4_categories() {
        var catalog = EvalCatalogDefinition.GetCatalog();

        await Assert.That(catalog).IsNotNull();
        await Assert.That(catalog.RetrospectivePrompt).IsNotNull();
        await Assert.That(catalog.RetrospectivePromptVersion).IsEqualTo("v3.1");
        await Assert.That(catalog.Questions.Count).IsEqualTo(13);

        var ids = string.Join(",", catalog.Questions.Select(q => $"{q.Category}/{q.Id}"));
        await Assert.That(ids).IsEqualTo(
            "safety/destructive_commands,safety/sensitive_files,safety/security_vulnerabilities,safety/permission_bypass,"
            + "plan_adherence/plan_adherence,plan_adherence/milestone_completion,plan_adherence/unapproved_scope_changes,"
            + "quality/tests_written,quality/broken_tests,quality/well_scoped_tasks,"
            + "efficiency/redundant_calls,efficiency/direct_approach,efficiency/unnecessary_exploration");

        var categories = catalog.Questions.Select(q => q.Category).Distinct().ToList();
        await Assert.That(categories).Contains("safety");
        await Assert.That(categories).Contains("quality");
        await Assert.That(categories).Contains("plan_adherence");
        await Assert.That(categories).Contains("efficiency");
    }

    [Test]
    public async Task GetCatalog_prompts_embed_the_runtime_placeholders() {
        var catalog = EvalCatalogDefinition.GetCatalog();

        await Assert.That(catalog.RetrospectivePrompt).Contains("{TRACE_JSON}");
        await Assert.That(catalog.RetrospectivePrompt).Contains("{VERDICTS_JSON}");
        await Assert.That(catalog.RetrospectivePrompt).Contains("{SESSION_META}");

        foreach (var q in catalog.Questions) {
            await Assert.That(q.Prompt).Contains("{TRACE_JSON}");
            await Assert.That(q.Prompt).Contains("{SESSION_ID}");
        }
    }

    [Test]
    public async Task GetCatalog_routes_exactly_the_pinned_tools_questions() {
        var catalog = EvalCatalogDefinition.GetCatalog();
        var toolsRouted = catalog.Questions.Where(q => q.NeedsTools).Select(q => (q.Category, q.Id)).ToList();

        await Assert.That(toolsRouted).IsEquivalentTo([
            ("safety", "destructive_commands"),
            ("quality", "tests_written"),
            ("quality", "broken_tests"),
            ("efficiency", "direct_approach")
        ]);
    }
}
