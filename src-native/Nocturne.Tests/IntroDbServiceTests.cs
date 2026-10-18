using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class IntroDbServiceTests
{
    [Fact]
    public void IntroDbService_InstantiatesSuccessfully()
    {
        var settings = new SettingsService();
        var introService = new IntroDbService(settings);

        Assert.NotNull(introService);
    }

    [Theory]
    [InlineData("intro", "Skip Intro (Enter)")]
    [InlineData("recap", "Skip Recap (Enter)")]
    [InlineData("outro", "Next Episode (Enter)")]
    public void SkipPromptEvent_FormatsLabelCorrectly(string segmentType, string expected)
    {
        var isOutro = segmentType == "outro";
        var label = segmentType switch
        {
            "recap" => "Skip Recap",
            "outro" => "Next Episode",
            _ => "Skip Intro"
        };
        var prompt = new SkipPromptEvent
        {
            Segment = new IntroSegment { Type = segmentType, StartSec = 10, EndSec = 45 },
            ActionLabel = $"{label} (Enter)",
            IsNextEpisode = isOutro
        };

        Assert.Equal(expected, prompt.ActionLabel);
        Assert.Equal(isOutro, prompt.IsNextEpisode);
    }
}
