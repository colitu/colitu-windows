using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>The Adaptive Connect 3.0 off-switch: hinted start and recovery set follow one constant.</summary>
public class ColituAdaptiveConnect3SwitchTests
{
    [Fact]
    public void ThisRelease_ShipsWithAdaptiveConnect3Off()
    {
        ColituAdaptiveConnect3.Enabled.Should().BeFalse();
        ColituAdaptiveConnect3.HintedStartActive(marksIgnored: false).Should().BeFalse();
        ColituAdaptiveConnect3.RecoveryFetchAllowed().Should().BeFalse();
        ColituAdaptiveConnect3.ShouldUseRecovery(automatic: true, apiUnreachableOnEveryBase: true, cacheUsable: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void HintedStart_NeedsTheSwitchAndNoIgnoredMarks(bool enabled, bool marksIgnored, bool expected)
    {
        ColituAdaptiveConnect3.HintedStartActive(enabled, marksIgnored).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RecoveryFetch_FollowsTheSwitch(bool enabled, bool expected)
    {
        ColituAdaptiveConnect3.RecoveryFetchAllowed(enabled).Should().Be(expected);
    }

    [Theory]
    // enabled, automatic, apiUnreachable, cacheUsable, expected
    [InlineData(true, true, true, false, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, false, false, false, false)]
    public void RecoveryUse_NeedsTheSwitchAndTheUsualConditions(bool enabled, bool automatic, bool unreachable, bool cacheUsable, bool expected)
    {
        ColituAdaptiveConnect3.ShouldUseRecovery(enabled, automatic, unreachable, cacheUsable).Should().Be(expected);
        if (enabled)
        {
            expected.Should().Be(ColituRecoverySet.ShouldUse(automatic, unreachable, cacheUsable));
        }
    }
}
