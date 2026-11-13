using GitlabCloner.Cli.Sync;
using Xunit;

namespace GitlabCloner.Tests;

public class PathMapperTests
{
    [Fact]
    public void ToLocalPath_maps_group_hierarchy_to_folder_tree()
    {
        var result = PathMapper.ToLocalPath(@"C:\repos", "platform/backend/order-service");

        Assert.Equal(Path.Combine(@"C:\repos", "platform", "backend", "order-service"), result);
    }

    [Fact]
    public void ToLocalPath_supports_single_level_project()
    {
        var result = PathMapper.ToLocalPath(@"C:\repos", "group/project");

        Assert.Equal(Path.Combine(@"C:\repos", "group", "project"), result);
    }

    [Fact]
    public void ToLocalPath_throws_for_empty_path()
    {
        Assert.Throws<ArgumentException>(() => PathMapper.ToLocalPath(@"C:\repos", ""));
    }

    [Fact]
    public void ToLocalPath_does_not_allow_escaping_the_root()
    {
        var root = @"C:\repos";

        var result = PathMapper.ToLocalPath(root, "../../windows/system32");

        Assert.StartsWith(Path.GetFullPath(root), result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a:b", "a_b")]
    [InlineData("question?mark", "question_mark")]
    [InlineData("star*", "star_")]
    public void SanitizeSegment_replaces_invalid_characters(string input, string expected)
    {
        Assert.Equal(expected, PathMapper.SanitizeSegment(input));
    }

    [Fact]
    public void SanitizeSegment_trims_trailing_dot()
    {
        Assert.Equal("project", PathMapper.SanitizeSegment("project."));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    public void SanitizeSegment_renames_windows_reserved_names(string reserved)
    {
        Assert.Equal(reserved + "_", PathMapper.SanitizeSegment(reserved));
    }

    [Fact]
    public void SanitizeSegment_leaves_normal_name_unchanged()
    {
        Assert.Equal("order-service", PathMapper.SanitizeSegment("order-service"));
    }
}
