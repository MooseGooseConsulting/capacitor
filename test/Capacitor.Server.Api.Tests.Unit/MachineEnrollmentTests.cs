namespace Capacitor.Server.Api.Tests.Unit;

public class MachineEnrollmentTests {
    [Test]
    public async Task MachineEnrollmentRequest_instantiates_with_valid_parameters() {
        var req = new MachineEnrollmentRequest(
            MachineId: "node-ser10",
            Hostname: "ser10.lan",
            Os: "linux",
            Arch: "x64"
        );

        await Assert.That(req.MachineId).IsEqualTo("node-ser10");
        await Assert.That(req.Hostname).IsEqualTo("ser10.lan");
        await Assert.That(req.Os).IsEqualTo("linux");
        await Assert.That(req.Arch).IsEqualTo("x64");
    }
}
