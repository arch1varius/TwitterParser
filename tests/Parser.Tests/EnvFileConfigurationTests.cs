using Microsoft.Extensions.Configuration;
using Parser.Api;
using Parser.Infrastructure;
using Xunit;

namespace Parser.Tests;

public sealed class EnvFileConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"parser-env-{Guid.NewGuid():N}");

    public EnvFileConfigurationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void EmptyAndMissingFilesDoNotOverrideConfiguration()
    {
        var file = Path.Combine(directory, ".env");
        Assert.Empty(EnvFileConfiguration.Read(file));
        File.WriteAllText(file, "");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Parser:Headless"] = "false" })
            .AddProjectEnvFile(directory).Build();
        Assert.False(config.GetValue<bool>("Parser:Headless"));
    }

    [Fact]
    public void ReadsQuotedConnectionStringCommentsAndLiteralSecrets()
    {
        File.WriteAllText(Path.Combine(directory, ".env"), """
            # local settings
            export ConnectionStrings__Parser = 'Host=localhost;Password=a=b#c $literal' # comment
            Parser__AuthToken="test-token"
            Parser__CsrfToken=
            Parser__Headless=false # visible browser
            Parser__ScrollDelayMs=10000
            Literal='${UNCHANGED}\path'
            Hash=a#b
            Empty= # comment
            """);
        var config = new ConfigurationBuilder().AddProjectEnvFile(directory).Build();
        Assert.Equal("Host=localhost;Password=a=b#c $literal", config.GetConnectionString("Parser"));
        var options = config.GetSection("Parser").Get<ParserOptions>()!;
        Assert.Equal("test-token", options.AuthToken);
        Assert.Equal("", options.CsrfToken);
        Assert.False(options.Headless);
        Assert.Equal(10000, options.ScrollDelayMs);
        Assert.Equal("${UNCHANGED}\\path", config["Literal"]);
        Assert.Equal("a#b", config["Hash"]);
        Assert.Equal("", config["Empty"]);
    }

    [Fact]
    public void SolutionRootIsUsedFromApiAndBuildDirectories()
    {
        File.WriteAllText(Path.Combine(directory, "Parser.slnx"), "<Solution />");
        var api = Path.Combine(directory, "src", "Parser.Api");
        var build = Path.Combine(api, "bin", "Release", "net10.0");
        Directory.CreateDirectory(build);
        var expected = Path.Combine(directory, ".env");
        Assert.Equal(expected, EnvFileConfiguration.FindPath(directory));
        Assert.Equal(expected, EnvFileConfiguration.FindPath(api));
        Assert.Equal(expected, EnvFileConfiguration.FindPath(build));
        Assert.Equal(expected, EnvFileConfiguration.FindPath(directory));
    }

    [Fact]
    public void PublishedAppUsesItsContentRoot()
        => Assert.Equal(Path.Combine(directory, ".env"), EnvFileConfiguration.FindPath(directory));

    [Fact]
    public void EnvironmentOverridesFileAndFileOverridesEarlierSettings()
    {
        File.WriteAllText(Path.Combine(directory, ".env"), "Parser__AuthToken=file-token\nParser__Headless=false");
        var prefix = $"PARSER_TEST_{Guid.NewGuid():N}_";
        var variable = prefix + "Parser__AuthToken";
        Environment.SetEnvironmentVariable(variable, "environment-token");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Parser:AuthToken"] = "earlier-token", ["Parser:Headless"] = "true" })
                .AddProjectEnvFile(directory).AddEnvironmentVariables(prefix).Build();
            Assert.Equal("environment-token", config["Parser:AuthToken"]);
            Assert.False(config.GetValue<bool>("Parser:Headless"));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Theory]
    [InlineData("missing-separator-secret")]
    [InlineData("=secret")]
    [InlineData("1BAD=secret")]
    [InlineData("BAD KEY=secret")]
    [InlineData("Parser__AuthToken='secret")]
    [InlineData("Parser__AuthToken=\"secret\" trailing")]
    public void InvalidLineReportsNumberWithoutLeakingValues(string invalid)
    {
        var file = Path.Combine(directory, ".env");
        File.WriteAllText(file, "# comment\n" + invalid);
        var error = Assert.Throws<FormatException>(() => EnvFileConfiguration.Read(file));
        Assert.Contains("2", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
