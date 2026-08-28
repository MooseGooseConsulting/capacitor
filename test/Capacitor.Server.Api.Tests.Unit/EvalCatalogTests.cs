namespace Capacitor.Server.Api.Tests.Unit;

public class EvalCatalogTests {
    [Test]
    public async Task GetCatalog_returns_complete_taxonomy_across_4_categories() {
        var catalog = EvalCatalogDefinition.GetCatalog();

        await Assert.That(catalog).IsNotNull();
        await Assert.That(catalog.RetrospectivePrompt).IsNotNull();
        await Assert.That(catalog.RetrospectivePromptVersion).IsEqualTo("v3.1");
        await Assert.That(catalog.Questions.Count).IsEqualTo(11);

        var categories = catalog.Questions.Select(q => q.Category).Distinct().ToList();
        await Assert.That(categories).Contains("Safety");
        await Assert.That(categories).Contains("Quality");
        await Assert.That(categories).Contains("Plan Adherence");
        await Assert.That(categories).Contains("Efficiency");
    }
}
