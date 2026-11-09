using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Varco.Core.Update;

namespace Varco.Tests;

/// <summary>Firma delle release e ciclo completo dell'aggiornamento con un finto GitHub in memoria.</summary>
public class UpdateTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _pub;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "varco-update-test-" + Guid.NewGuid().ToString("N"));

    public UpdateTests()
    {
        _pub = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string FakeExe(string content = "finto Varco.exe")
    {
        var path = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("v0.3.0", 0, 3, 0)]
    [InlineData("0.3", 0, 3, 0)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    [InlineData("V2.0.1+abc123", 2, 0, 1)]
    public void Versions_are_normalized_to_three_parts(string text, int major, int minor, int build)
    {
        Assert.True(UpdateSignature.TryParseVersion(text, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void Bad_versions_are_rejected(string? text) => Assert.False(UpdateSignature.TryParseVersion(text, out _));

    [Fact]
    public void Signature_roundtrip_and_tampering()
    {
        var m = UpdateSignature.Sign(FakeExe(), new Version(0, 3, 0), _key);
        Assert.Equal("0.3.0", m.Version);
        Assert.True(UpdateSignature.Verify(m, _pub));
        var copy = UpdateSignature.Deserialize(UpdateSignature.Serialize(m))!;
        Assert.True(UpdateSignature.Verify(copy, _pub));

        Assert.False(UpdateSignature.Verify(With(m, version: "9.9.9"), _pub));
        Assert.False(UpdateSignature.Verify(With(m, sha: new string('0', 64)), _pub));
        Assert.False(UpdateSignature.Verify(With(m, size: m.Size + 1), _pub));
        // Firmato con un'altra chiave (es. la chiave pubblica vera di Varco): rifiutato.
        Assert.False(UpdateSignature.Verify(m));
    }

    private static UpdateManifest With(UpdateManifest m, string? version = null, string? sha = null, long? size = null) => new()
    {
        Version = version ?? m.Version,
        Sha256 = sha ?? m.Sha256,
        Size = size ?? m.Size,
        Signature = m.Signature,
    };

    [Fact]
    public void Release_parsing_needs_both_assets_and_skips_prereleases()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        var ok = Updater.ParseRelease(J("""
            {"tag_name":"v0.4.1","assets":[
              {"name":"Varco.exe","browser_download_url":"https://x/Varco.exe"},
              {"name":"Varco.exe.sig","browser_download_url":"https://x/Varco.exe.sig"}]}
            """));
        Assert.NotNull(ok);
        Assert.Equal(new Version(0, 4, 1), ok.Version);
        Assert.Equal("https://x/Varco.exe.sig", ok.SigUrl);

        Assert.Null(Updater.ParseRelease(J("""{"tag_name":"v0.4.1","assets":[{"name":"Varco.exe","browser_download_url":"https://x/a"}]}""")));
        Assert.Null(Updater.ParseRelease(J("""{"tag_name":"v0.4.1","prerelease":true,"assets":[]}""")));
    }

    // ───────────────────────────── ciclo completo ─────────────────────────────

    /// <summary>Finto GitHub: risponde a /latest, /Varco.exe e /Varco.exe.sig.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string Tag = "v0.3.0";
        public byte[] Exe = [];
        public string Sig = "";
        public int ExeDownloads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage r;
            if (path.EndsWith("/latest"))
                r = Text($$"""
                    {"tag_name":"{{Tag}}","assets":[
                      {"name":"Varco.exe","browser_download_url":"https://fake/dl/Varco.exe"},
                      {"name":"Varco.exe.sig","browser_download_url":"https://fake/dl/Varco.exe.sig"}]}
                    """);
            else if (path.EndsWith("/Varco.exe.sig")) r = Text(Sig);
            else if (path.EndsWith("/Varco.exe"))
            {
                ExeDownloads++;
                r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Exe) };
            }
            else r = new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(r);
        }

        private static HttpResponseMessage Text(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8) };
    }

    private (FakeGitHub Gh, List<string> Launched, Func<bool, Updater> Make) Setup(string version = "0.3.0", string content = "nuovo Varco")
    {
        var src = FakeExe(content);
        var gh = new FakeGitHub
        {
            Tag = "v" + version,
            Exe = File.ReadAllBytes(src),
            Sig = UpdateSignature.Serialize(UpdateSignature.Sign(src, Version.Parse(version), _key)),
        };
        var launched = new List<string>();
        Updater Make(bool canInstall) => new(new UpdaterOptions
        {
            FeedUrl = "https://fake/repos/x/releases/latest",
            CurrentVersion = new Version(0, 2, 0),
            DownloadDirectory = Path.Combine(_dir, "updates"),
            PublicKey = _pub,
            Handler = gh,
            ProtectDirectory = false,
            CanInstallNow = () => canInstall,
            Launch = launched.Add,
        });
        return (gh, launched, Make);
    }

    [Fact]
    public async Task Newer_signed_release_is_downloaded_verified_and_launched()
    {
        var (gh, launched, make) = Setup();
        using var u = make(true);
        var v = await u.CheckAndInstallAsync();
        Assert.Equal(new Version(0, 3, 0), v);
        var exe = Assert.Single(launched);
        Assert.Equal("nuovo Varco", File.ReadAllText(exe));
        Assert.Equal("Installing", u.Status.State);
    }

    [Fact]
    public async Task Same_or_older_release_is_ignored()
    {
        var (gh, launched, make) = Setup(version: "0.2.0");
        using var u = make(true);
        Assert.Null(await u.CheckAndInstallAsync());
        Assert.Empty(launched);
        Assert.Equal(0, gh.ExeDownloads);
        Assert.Equal("UpToDate", u.Status.State);
    }

    [Fact]
    public async Task Install_waits_while_another_pc_is_in_use()
    {
        var (gh, launched, make) = Setup();
        var busy = true;
        using var u = new Updater(new UpdaterOptions
        {
            FeedUrl = "https://fake/repos/x/releases/latest",
            CurrentVersion = new Version(0, 2, 0),
            DownloadDirectory = Path.Combine(_dir, "updates"),
            PublicKey = _pub,
            Handler = gh,
            ProtectDirectory = false,
            CanInstallNow = () => !busy,
            Launch = launched.Add,
        });
        Assert.Null(await u.CheckAndInstallAsync());
        Assert.Equal("Waiting", u.Status.State);
        Assert.Empty(launched);

        busy = false;
        Assert.Equal(new Version(0, 3, 0), await u.CheckAndInstallAsync());
        Assert.Single(launched);
        Assert.Equal(1, gh.ExeDownloads); // non lo riscarica
    }

    [Fact]
    public async Task Tampered_exe_or_foreign_signature_is_never_launched()
    {
        var (gh, launched, make) = Setup();
        gh.Exe = Encoding.UTF8.GetBytes("Varco con sorpresa");
        using (var u = make(true))
            await Assert.ThrowsAnyAsync<Exception>(() => u.CheckAndInstallAsync());

        // Release firmata con un'altra chiave.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var src = FakeExe("nuovo Varco");
        gh.Exe = File.ReadAllBytes(src);
        gh.Sig = UpdateSignature.Serialize(UpdateSignature.Sign(src, new Version(0, 3, 0), other));
        using (var u = make(true))
            await Assert.ThrowsAnyAsync<Exception>(() => u.CheckAndInstallAsync());

        // Firma valida ma per una versione diversa da quella della release (exe vecchio spacciato per nuovo).
        gh.Sig = UpdateSignature.Serialize(UpdateSignature.Sign(src, new Version(0, 2, 5), _key));
        using (var u = make(true))
            await Assert.ThrowsAnyAsync<Exception>(() => u.CheckAndInstallAsync());

        Assert.Empty(launched);
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "updates")) ? Directory.GetFiles(Path.Combine(_dir, "updates"), "*.exe") : []);
    }
}
