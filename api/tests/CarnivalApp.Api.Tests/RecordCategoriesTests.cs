using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Records;
using Xunit;

namespace CarnivalApp.Api.Tests;

public sealed class RecordCategoriesTests
{
    [Fact]
    public void A_blob_of_ages_and_categories_reads_as_their_pairs()
    {
        var pairs = RecordCategories.Parse("""{"ageGroups":["13"],"categories":["M","F"]}""");
        Assert.Equal(
            new HashSet<(string, CompetitionCategory)> { ("13", CompetitionCategory.M), ("13", CompetitionCategory.F) },
            pairs);
    }

    [Fact]
    public void A_stored_blob_naming_open_still_reads_its_other_ages_and_is_refused_on_write()
    {
        var json = """{"ageGroups":["13"," open "],"categories":["M"]}""";
        Assert.Equal(
            new HashSet<(string, CompetitionCategory)> { ("13", CompetitionCategory.M) },
            RecordCategories.Parse(json));
        Assert.NotNull(RecordCategories.OpenAgeRefusal(json));
    }

    [Fact]
    public void A_stored_blob_naming_only_open_reads_as_no_categories()
    {
        var json = """{"ageGroups":["OPEN"],"categories":["M"]}""";
        Assert.Null(RecordCategories.Parse(json));
        Assert.NotNull(RecordCategories.OpenAgeRefusal(json));
    }

    [Fact]
    public void A_blob_without_open_is_not_refused()
    {
        Assert.Null(RecordCategories.OpenAgeRefusal("""{"ageGroups":["13","SNR"],"categories":["M"]}"""));
        Assert.Null(RecordCategories.OpenAgeRefusal(null));
    }
}
