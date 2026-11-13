using GitlabCloner.Cli.GitLab;
using Xunit;

namespace GitlabCloner.Tests;

public class LinkHeaderParserTests
{
    [Fact]
    public void Finds_next_link()
    {
        const string header =
            "<https://gitlab.local/api/v4/projects?id_after=42&per_page=100>; rel=\"next\", " +
            "<https://gitlab.local/api/v4/projects?per_page=100>; rel=\"first\"";

        var next = LinkHeaderParser.FindRel(header, "next");

        Assert.Equal("https://gitlab.local/api/v4/projects?id_after=42&per_page=100", next);
    }

    [Fact]
    public void Returns_null_on_last_page_without_next()
    {
        const string header = "<https://gitlab.local/api/v4/projects?per_page=100>; rel=\"first\"";

        Assert.Null(LinkHeaderParser.FindRel(header, "next"));
    }

    [Fact]
    public void Commas_inside_url_do_not_split_links()
    {
        const string header = "<https://gitlab.local/api/v4/projects?order_by=id,name>; rel=\"next\"";

        Assert.Equal("https://gitlab.local/api/v4/projects?order_by=id,name", LinkHeaderParser.FindRel(header, "next"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("broken-header")]
    public void Returns_null_for_invalid_input(string? header)
    {
        Assert.Null(LinkHeaderParser.FindRel(header, "next"));
    }
}
