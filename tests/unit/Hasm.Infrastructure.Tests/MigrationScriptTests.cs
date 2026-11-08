using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

public class MigrationScriptTests
{
    [Fact]
    public void Embedded_scripts_load_ordered_and_contiguous()
    {
        var scripts = MigrationScript.LoadEmbedded();
        Assert.NotEmpty(scripts);
        Assert.Equal(Enumerable.Range(1, scripts.Count), scripts.Select(s => s.Version));
    }

    [Fact]
    public void Checksum_is_stable_and_ignores_line_ending_style()
    {
        var lf = MigrationScript.Parse("0001_a.sql", "CREATE TABLE a (x int);\nSELECT 1;\n");
        var crlf = MigrationScript.Parse("0001_a.sql", "CREATE TABLE a (x int);\r\nSELECT 1;\r\n");
        Assert.Equal(lf.Checksum, crlf.Checksum);
        Assert.Equal(64, lf.Checksum.Length);
        Assert.NotEqual(lf.Checksum, MigrationScript.Parse("0001_a.sql", "CREATE TABLE b (x int);").Checksum);
    }

    [Theory]
    [InlineData("DROP TABLE x;")]
    [InlineData("drop database x;")]
    [InlineData("TRUNCATE x;")]
    [InlineData("DELETE FROM x;")]
    [InlineData("ALTER TABLE x DROP COLUMN y;")]
    public void Destructive_statements_are_rejected(string sql) =>
        Assert.Throws<MigrationException>(() => MigrationScript.Parse("0002_x.sql", sql));

    [Fact]
    public void Destructive_words_in_comments_are_ignored() =>
        MigrationScript.Parse("0002_x.sql", "-- we never DROP anything\nCREATE TABLE x (a int);");

    [Theory]
    [InlineData("1_x.sql")]
    [InlineData("0001_X.sql")]
    [InlineData("0001.sql")]
    [InlineData("0001_x.txt")]
    public void Invalid_file_names_are_rejected(string name) =>
        Assert.Throws<MigrationException>(() => MigrationScript.Parse(name, "SELECT 1;"));

    [Fact]
    public void Gaps_and_duplicates_are_rejected()
    {
        var a = MigrationScript.Parse("0001_a.sql", "SELECT 1;");
        var c = MigrationScript.Parse("0003_c.sql", "SELECT 1;");
        Assert.Throws<MigrationException>(() => MigrationScript.Validate([a, c]));
        Assert.Throws<MigrationException>(() => MigrationScript.Validate([a, a]));
    }
}
