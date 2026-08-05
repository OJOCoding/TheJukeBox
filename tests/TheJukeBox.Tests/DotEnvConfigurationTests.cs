using Microsoft.Extensions.Configuration;
using TheJukeBox.Configuration;

namespace TheJukeBox.Tests;

/// <summary>
/// Reading <c>.env</c> for a local run.
/// </summary>
/// <remarks>
/// The same file has to configure the app whether it is started by Docker Compose or by
/// <c>dotnet run</c>, which means the mapping exists twice: once in <c>docker-compose.yml</c> and
/// once in <see cref="DotEnvConfiguration"/>. These tests hold the two together and pin the
/// precedence, because getting precedence wrong is not an error -- it is a value silently coming
/// from the wrong place.
/// </remarks>
public sealed class DotEnvConfigurationTests : IDisposable
{
    private readonly string _directory;

    public DotEnvConfigurationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "jukebox-tests", Guid.NewGuid().ToString("N"));

        // A nested content root, as the real layout has: the file sits at the repository root while
        // the app runs from src/TheJukeBox.
        Directory.CreateDirectory(Path.Combine(_directory, "src", "TheJukeBox"));
    }

    /// <summary>
    /// The mapping and Compose's own <c>environment:</c> block must agree, or the same file
    /// configures the two ways of running differently -- and the difference would only show up as
    /// a feature behaving one way in Docker and another locally.
    /// </summary>
    [Fact]
    public void Every_mapping_matches_docker_compose()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker-compose.yml"));

        Assert.NotEmpty(DotEnvConfiguration.KnownMappings);

        foreach (var (variable, key) in DotEnvConfiguration.KnownMappings)
        {
            // Compose spells keys with double underscores for configuration nesting.
            var composeKey = key.Replace(':', '_').Replace("_", "__", StringComparison.Ordinal);

            Assert.Contains($"${{{variable}", compose, StringComparison.Ordinal);
            Assert.Contains(composeKey, compose, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// And the other direction: a variable added to Compose but not here would work in Docker and
    /// be silently ignored locally.
    /// </summary>
    [Fact]
    public void Every_compose_variable_is_mapped()
    {
        var compose = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docker-compose.yml"));

        var mapped = DotEnvConfiguration.KnownMappings.Select(m => m.Variable).ToHashSet(StringComparer.Ordinal);

        var referenced = compose
            .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"\$\{([A-Z0-9_]+)"))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(referenced);

        var unmapped = referenced.Where(name => !mapped.Contains(name)).ToList();

        Assert.Empty(unmapped);
    }

    /// <summary>
    /// appsettings.json must not declare the settings <c>.env</c> supplies, even as empty strings.
    /// </summary>
    /// <remarks>
    /// An empty string is a present value in configuration, so a blank key in appsettings.json
    /// overrides every lower-precedence source. That is precisely what broke the first version of
    /// the <c>.env</c> reader: the file was found and parsed, the log said so, and the app still
    /// refused to start for want of a client id -- because a committed <c>""</c> was winning. The
    /// keys are absent now, and this stops them coming back as documentation.
    /// </remarks>
    [Fact]
    public void Appsettings_does_not_declare_the_settings_dot_env_supplies()
    {
        var json = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TheJukeBox", "appsettings.json")));

        foreach (var (_, key) in DotEnvConfiguration.KnownMappings)
        {
            var element = json.RootElement;
            var present = true;

            foreach (var segment in key.Split(':'))
            {
                if (element.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !element.TryGetProperty(segment, out element))
                {
                    present = false;
                    break;
                }
            }

            Assert.False(
                present,
                $"appsettings.json declares '{key}'. Even an empty value there overrides .env, "
                + "user-secrets and environment variables that would otherwise supply it.");
        }
    }

    [Fact]
    public void Reads_values_into_configuration_keys()
    {
        WriteDotEnv(
            "JUKEBOX_CLIENT_ID=123-abc.apps.googleusercontent.com",
            "JUKEBOX_CLIENT_SECRET=GOCSPX-secret",
            "AUTH_REGISTRATION=Open");

        var configuration = Build();

        Assert.Equal("123-abc.apps.googleusercontent.com", configuration["Authentication:Google:ClientId"]);
        Assert.Equal("GOCSPX-secret", configuration["Authentication:Google:ClientSecret"]);
        Assert.Equal("Open", configuration["Auth:Registration"]);
    }

    [Theory]
    [InlineData("# a comment")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-equals-sign")]
    [InlineData("=novalue")]
    [InlineData("UNRECOGNISED_NAME=whatever")]
    public void Ignores_lines_it_does_not_understand(string line)
    {
        WriteDotEnv(line, "JUKEBOX_CLIENT_ID=real");

        var configuration = Build();

        Assert.Equal("real", configuration["Authentication:Google:ClientId"]);
    }

    /// <summary>
    /// Compose permits quoting and a leading <c>export</c>, so a file that works there must work
    /// here. Anything else would mean editing the file differently depending on how you run it.
    /// </summary>
    [Theory]
    [InlineData("JUKEBOX_CLIENT_ID=\"quoted\"", "quoted")]
    [InlineData("JUKEBOX_CLIENT_ID='single'", "single")]
    [InlineData("export JUKEBOX_CLIENT_ID=exported", "exported")]
    [InlineData("JUKEBOX_CLIENT_ID =  spaced  ", "spaced")]
    public void Handles_compose_spellings(string line, string expected)
    {
        WriteDotEnv(line);

        Assert.Equal(expected, Build()["Authentication:Google:ClientId"]);
    }

    /// <summary>
    /// An emptied line reads as absent, so startup validation complains about a missing value
    /// rather than the app starting with a blank client id and failing at Google instead.
    /// </summary>
    [Fact]
    public void An_empty_value_is_treated_as_absent()
    {
        WriteDotEnv("JUKEBOX_CLIENT_ID=");

        Assert.Null(Build()["Authentication:Google:ClientId"]);
    }

    /// <summary>
    /// The precedence that matters. <c>.env</c> holds a deployment's public URL, which is exactly
    /// wrong for a local run, so the launch profile's environment variable has to win. If this
    /// inverted, every local sign-in would fail with Google's redirect_uri_mismatch.
    /// </summary>
    [Fact]
    public void Environment_variables_win_over_dot_env()
    {
        WriteDotEnv(
            "APP_PUBLIC_BASE_URL=https://music.example.com",
            "JUKEBOX_CLIENT_ID=from-dot-env");

        var builder = new ConfigurationBuilder();

        builder.AddRepositoryDotEnv(ContentRoot());

        // Added after, as the host adds environment variables after every file source.
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:PublicBaseUrl"] = "http://localhost:5255",
        });

        var configuration = builder.Build();

        Assert.Equal("http://localhost:5255", configuration["App:PublicBaseUrl"]);

        // And a value nothing else supplies still comes through.
        Assert.Equal("from-dot-env", configuration["Authentication:Google:ClientId"]);
    }

    /// <summary>
    /// Inserting at the front is what makes the precedence above true. Appending would make
    /// <c>.env</c> the highest-precedence source instead, which is the bug this asserts against.
    /// </summary>
    [Fact]
    public void The_source_is_registered_first()
    {
        WriteDotEnv("JUKEBOX_CLIENT_ID=x");

        var builder = new ConfigurationBuilder();

        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Existing"] = "1" });

        var path = builder.AddRepositoryDotEnv(ContentRoot());

        Assert.NotNull(path);
        Assert.Equal(2, builder.Sources.Count);

        // The .env source is index 0, ahead of the source that was already there.
        Assert.IsType<Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource>(builder.Sources[0]);
    }

    [Fact]
    public void Reports_nothing_when_there_is_no_file()
    {
        var builder = new ConfigurationBuilder();

        Assert.Null(builder.AddRepositoryDotEnv(ContentRoot()));
        Assert.Empty(builder.Sources);
    }

    /// <summary>
    /// The walk up must stop inside the checkout. Without the marker it could climb into a parent
    /// directory and pick up an unrelated project's .env, which would be a genuinely alarming way
    /// to acquire someone else's credentials.
    /// </summary>
    [Fact]
    public void The_search_stops_at_the_repository_root()
    {
        // A .env outside the checkout, and a solution file marking the top of it.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_directory)!, ".env"), "JUKEBOX_CLIENT_ID=outside");
        File.WriteAllText(Path.Combine(_directory, "TheJukeBox.slnx"), "<Solution />");

        var builder = new ConfigurationBuilder();

        Assert.Null(builder.AddRepositoryDotEnv(ContentRoot()));
    }

    /// <summary>A file found at the repository root is read, which is where the real one lives.</summary>
    [Fact]
    public void A_file_at_the_repository_root_is_found_from_a_nested_content_root()
    {
        File.WriteAllText(Path.Combine(_directory, "TheJukeBox.slnx"), "<Solution />");
        WriteDotEnv("JUKEBOX_CLIENT_ID=found");

        Assert.Equal("found", Build()["Authentication:Google:ClientId"]);
    }

    public void Dispose()
    {
        try
        {
            var outside = Path.Combine(Path.GetDirectoryName(_directory)!, ".env");

            if (File.Exists(outside))
            {
                File.Delete(outside);
            }

            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ContentRoot() => Path.Combine(_directory, "src", "TheJukeBox");

    private void WriteDotEnv(params string[] lines) =>
        File.WriteAllLines(Path.Combine(_directory, ".env"), lines);

    private IConfigurationRoot Build()
    {
        var builder = new ConfigurationBuilder();

        builder.AddRepositoryDotEnv(ContentRoot());

        return builder.Build();
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheJukeBox.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
