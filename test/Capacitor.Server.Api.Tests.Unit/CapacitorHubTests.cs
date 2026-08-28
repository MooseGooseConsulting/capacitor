namespace Capacitor.Server.Api.Tests.Unit;

public class CapacitorHubTests {
    [Test]
    public async Task CapacitorHub_can_be_instantiated() {
        var hub = new CapacitorHub();
        await Assert.That(hub).IsNotNull();
    }
}
