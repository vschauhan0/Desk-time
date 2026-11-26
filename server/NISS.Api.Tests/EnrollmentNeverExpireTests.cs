using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class EnrollmentNeverExpireTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Never_expiring_code_has_no_expiry_and_still_enrolls()
    {
        var admin = await factory.AdminAsync();

        var token = await admin.CreateTokenAsync("EMP-NEVER", hours: 0); // 0 hours = never expires

        Assert.Null(token.ExpiresAtUtc);

        using var anonymous = factory.CreateClient();
        var enrolled = await (await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!)))
            .ReadAsync<EnrollResponse>();

        Assert.Matches(@"^PC-\d{3,}$", enrolled.DeviceId);
        Assert.Equal("EMP-NEVER", enrolled.EmployeeId);
    }
}
