namespace Querithm.Tests.Security;

/// <summary>
/// L33: the dev fixture's credentials live only in scripts/bench/docker (compose.yml and ssh/, CLAUDE.md §3); tests and
/// scripts read them from there. Messages name the file and line, never a value.
/// </summary>
public class FixtureSecretTests
{
    const string Compose = """
        # header comment
        name: example
        services:
          db:
            image: example
            environment:
              ACCEPT: "Y"
              DB_PASSWORD: "quoted value!"
          other:
            environment:
              DB_PASSWORD: plain-value  # a comment
          bastion:
            build:
              context: .
              args:
                # a comment inside the block
                SSH_PASSWORD: 'single'
            ports: ["127.0.0.1:1:2"]
        """;

    [Fact]
    public void The_compose_reader_takes_a_value_from_one_services_block_only()
    {
        Assert.Equal("quoted value!", FixtureSecrets.ComposeValue(Compose, "db", "DB_PASSWORD"));
        Assert.Equal("plain-value", FixtureSecrets.ComposeValue(Compose, "other", "DB_PASSWORD"));
        Assert.Equal("single", FixtureSecrets.ComposeValue(Compose, "bastion", "SSH_PASSWORD"));
        Assert.Throws<InvalidOperationException>(() => FixtureSecrets.ComposeValue(Compose, "other", "SSH_PASSWORD"));
        Assert.Throws<InvalidOperationException>(() => FixtureSecrets.ComposeValue(Compose, "missing", "DB_PASSWORD"));
    }

    [Fact]
    public void The_fixture_credentials_are_found_in_the_fixture_files()
    {
        foreach (var engine in new[] { "mssql", "pg", "mysql", "ssh" })
            Assert.True(FixtureSecrets.Password(engine).Length >= 6, $"no {engine} password in compose.yml");
        Assert.True(FixtureSecrets.SshKeyPassphrase.Length >= 6, "no passphrase in ssh/test_rsa_pass.passphrase");
    }

    [Fact]
    public void The_fixture_credentials_appear_nowhere_but_the_fixture_files()
    {
        var docker = Path.GetDirectoryName(FixtureSecrets.FixtureFile("compose.yml"))!;
        var root = Path.GetFullPath(Path.Combine(docker, "..", "..", ".."));
        var compose = File.ReadAllText(Path.Combine(docker, "compose.yml"));
        var secrets = new Dictionary<string, string>
        {
            ["MSSQL_SA_PASSWORD"] = FixtureSecrets.ComposeValue(compose, "mssql", "MSSQL_SA_PASSWORD"),
            ["POSTGRES_PASSWORD"] = FixtureSecrets.ComposeValue(compose, "postgres", "POSTGRES_PASSWORD"),
            ["MYSQL_ROOT_PASSWORD"] = FixtureSecrets.ComposeValue(compose, "mysql84", "MYSQL_ROOT_PASSWORD"),
            ["MARIADB_ROOT_PASSWORD"] = FixtureSecrets.ComposeValue(compose, "mariadb118", "MARIADB_ROOT_PASSWORD"),
            ["SSH_PASSWORD"] = FixtureSecrets.ComposeValue(compose, "ssh", "SSH_PASSWORD"),
            ["ssh key passphrase"] = FixtureSecrets.SshKeyPassphrase,
        };
        string[] skipDirs = ["bin", "obj", "node_modules", "target", "dist", "out", ".git", "results", "gen"];
        string[] exts = [".cs", ".mjs", ".js", ".ts", ".tsx", ".rs", ".md", ".yml", ".yaml", ".json", ".ps1", ".sh", ".toml", ".sql", ".py", ".Dockerfile"];
        IEnumerable<string> Walk(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (exts.Contains(Path.GetExtension(f)) || Path.GetFileName(f).EndsWith("Dockerfile", StringComparison.Ordinal)) yield return f;
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (!skipDirs.Contains(Path.GetFileName(d)) && Path.GetFullPath(d) != Path.GetFullPath(docker))
                    foreach (var f in Walk(d)) yield return f;
        }
        var found = new List<string>();
        var dirs = new[] { "sidecar", "scripts", "src", Path.Combine("src-tauri", "src"), "docs", ".github" }.Select(d => Path.Combine(root, d)).Where(Directory.Exists);
        foreach (var file in dirs.SelectMany(Walk).Concat(Directory.EnumerateFiles(root).Where(f => exts.Contains(Path.GetExtension(f)))))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (var (name, value) in secrets)
                    if (value.Length >= 6 && lines[i].Contains(value, StringComparison.Ordinal))
                        found.Add($"{Path.GetRelativePath(root, file)}:{i + 1} holds the fixture's {name}");
        }
        Assert.True(found.Count == 0, string.Join("\n", found));
    }
}
