using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>SUPERADMIN uploads a signed agent package: verified server-side, stored on disk, indexed in agent_packages.</summary>
[Collection(ApiCollection.Name)]
public sealed class AgentUpdateUploadTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static byte[] Zip(string version, params (string Path, byte[] Bytes)[] files)
    {
        var manifest = new ReleaseManifest(version, null,
            files.Select(f => new ManifestFile(f.Path, Convert.ToHexString(SHA256.HashData(f.Bytes)).ToLowerInvariant())).ToList(),
            false, "notes");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] data) { using var s = zip.CreateEntry(name).Open(); s.Write(data); }
            Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJson.Options));
            foreach (var f in files) Add(f.Path, f.Bytes);
        }
        return ms.ToArray();
    }

    private static Task<HttpResponseMessage> Upload(HttpClient c, byte[] zip)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "package", "agent.zip");
        return c.PostAsync("/api/v1/agent-packages", form);
    }

    private static string NewVersion() => $"{Random.Shared.Next(100, 900)}.{Random.Shared.Next(0, 999)}.{Random.Shared.Next(0, 999)}.0";

    private async Task<int> CountAsync(string version)
    {
        using var scope = factory.Scope();
        return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().AgentPackages.CountAsync(p => p.Version == version);
    }

    [Fact]
    public async Task Accepted_package_is_stored_and_returns_the_version()
    {
        factory.Signatures.Accept = true;
        var admin = await factory.AdminAsync();
        var v = NewVersion();
        var response = await Upload(admin, Zip(v, ("NISS.Agent.exe", [1, 2, 3]), ("readme.txt", [9])));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.ReadAsync<AgentPackageDto>();
        Assert.Equal(v, dto.Version);
        Assert.Equal("admin", dto.UploadedBy);
        Assert.Equal(1, await CountAsync(v));
        // Same version again is a conflict, not a second row.
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(admin, Zip(v, ("NISS.Agent.exe", [1, 2, 3])))).StatusCode);
        Assert.Equal(1, await CountAsync(v));
    }

    [Fact]
    public async Task Package_the_signature_check_rejects_is_400_and_not_stored()
    {
        factory.Signatures.Accept = false;
        try
        {
            var admin = await factory.AdminAsync();
            var v = NewVersion();
            var response = await Upload(admin, Zip(v, ("NISS.Agent.exe", [1, 2, 3])));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await CountAsync(v));
        }
        finally { factory.Signatures.Accept = true; }
    }

    [Fact]
    public async Task Manifest_with_no_executable_is_400()
    {
        factory.Signatures.Accept = true;
        var admin = await factory.AdminAsync();
        var v = NewVersion();
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, Zip(v))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, Zip(v, ("readme.txt", [1])))).StatusCode);
        Assert.Equal(0, await CountAsync(v));
    }

    [Fact]
    public async Task Hash_mismatch_is_400()
    {
        factory.Signatures.Accept = true;
        var admin = await factory.AdminAsync();
        var v = NewVersion();
        var manifest = new ReleaseManifest(v, null, [new ManifestFile("NISS.Agent.exe", new string('0', 64))], false, null);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var s = zip.CreateEntry("manifest.json").Open()) s.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJson.Options));
            using (var s = zip.CreateEntry("NISS.Agent.exe").Open()) s.Write([1, 2, 3]);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, ms.ToArray())).StatusCode);
        Assert.Equal(0, await CountAsync(v));
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("VIEWER")]
    public async Task Non_superadmin_is_403(string role)
    {
        var superAdmin = await factory.AdminAsync();
        var client = await factory.UserWithRoleAsync(superAdmin, role);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(client, Zip(NewVersion(), ("NISS.Agent.exe", [1])))).StatusCode);
    }
}
