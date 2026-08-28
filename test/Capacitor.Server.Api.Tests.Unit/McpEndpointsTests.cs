namespace Capacitor.Server.Api.Tests.Unit;

public class McpEndpointsTests {
    [Test]
    public async Task McpRequest_instantiates_with_method_and_params() {
        var req = new McpRequest(
            Method: "search_sessions",
            Params: new Dictionary<string, object> { ["query"] = "refactor" }
        );

        await Assert.That(req.Method).IsEqualTo("search_sessions");
        await Assert.That(req.Params!["query"].ToString()).IsEqualTo("refactor");
    }
}
