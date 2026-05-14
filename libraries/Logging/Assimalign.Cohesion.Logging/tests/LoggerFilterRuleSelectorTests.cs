using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Logging.Internal;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerFilterRuleSelectorTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: returns null when rules list is empty")]
    public void NoRules_ReturnsNull()
    {
        var result = LoggerFilterRuleSelector.Select(
            Array.Empty<LoggerFilterRule>(),
            typeof(RecordingProvider),
            "App");
        Assert.Null(result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: rule matching provider type wins over untyped rule")]
    public void Step1_TypedRulesWin()
    {
        var typed = new LoggerFilterRule(providerType: typeof(RecordingProvider), level: LogLevel.Error);
        var untyped = new LoggerFilterRule(level: LogLevel.Trace);

        var result = LoggerFilterRuleSelector.Select(
            new[] { untyped, typed },
            typeof(RecordingProvider),
            "Any");

        Assert.Same(typed, result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: falls back to untyped rule when no typed match")]
    public void Step1_UntypedFallback()
    {
        var untyped = new LoggerFilterRule(level: LogLevel.Trace);

        var result = LoggerFilterRuleSelector.Select(
            new[] { untyped },
            typeof(RecordingProvider),
            "Any");

        Assert.Same(untyped, result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: longest matching category prefix wins")]
    public void Step2_LongestPrefixWins()
    {
        var shortRule = new LoggerFilterRule(category: "App", level: LogLevel.Error);
        var longRule = new LoggerFilterRule(category: "App.Network", level: LogLevel.Trace);

        var result = LoggerFilterRuleSelector.Select(
            new[] { shortRule, longRule },
            typeof(RecordingProvider),
            "App.Network.Http");

        Assert.Same(longRule, result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: prefix match is case-insensitive")]
    public void Step2_PrefixCaseInsensitive()
    {
        var rule = new LoggerFilterRule(category: "App.Network", level: LogLevel.Trace);

        var result = LoggerFilterRuleSelector.Select(
            new[] { rule },
            typeof(RecordingProvider),
            "APP.NETWORK.HTTP");

        Assert.Same(rule, result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: rules without category serve as default within candidate set")]
    public void Step3_NoCategoryFallback()
    {
        var withCategoryNoMatch = new LoggerFilterRule(category: "Other", level: LogLevel.Error);
        var noCategory = new LoggerFilterRule(level: LogLevel.Trace);

        var result = LoggerFilterRuleSelector.Select(
            new[] { withCategoryNoMatch, noCategory },
            typeof(RecordingProvider),
            "App.Network.Http");

        Assert.Same(noCategory, result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: single matching rule is used directly")]
    public void Step4_SingleMatchUsed()
    {
        var rule = new LoggerFilterRule(category: "App", level: LogLevel.Information);

        var result = LoggerFilterRuleSelector.Select(
            new[] { rule },
            typeof(RecordingProvider),
            "App.Network");

        Assert.Same(rule, result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: last rule wins among ties on longest prefix")]
    public void Step5_LastAmongTiesWins()
    {
        var first = new LoggerFilterRule(category: "App", level: LogLevel.Error);
        var second = new LoggerFilterRule(category: "App", level: LogLevel.Trace);
        var third = new LoggerFilterRule(category: "App", level: LogLevel.Warning);

        var result = LoggerFilterRuleSelector.Select(
            new[] { first, second, third },
            typeof(RecordingProvider),
            "App.Network");

        Assert.Same(third, result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: last untyped rule wins when no category matches")]
    public void Step5_LastUntypedFallback()
    {
        var first = new LoggerFilterRule(level: LogLevel.Error);
        var second = new LoggerFilterRule(level: LogLevel.Trace);

        var result = LoggerFilterRuleSelector.Select(
            new[] { first, second },
            typeof(RecordingProvider),
            "App.Network");

        Assert.Same(second, result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: returns null when no rule applies")]
    public void Step6_NoRulesApplicable()
    {
        // Only a rule with a different provider type; current provider has none.
        var rule = new LoggerFilterRule(providerType: typeof(AltRecordingProvider), level: LogLevel.Error);

        var result = LoggerFilterRuleSelector.Select(
            new[] { rule },
            typeof(RecordingProvider),
            "App");

        Assert.Null(result);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Selector: rejects null arguments")]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LoggerFilterRuleSelector.Select(null!, typeof(RecordingProvider), "X"));
        Assert.Throws<ArgumentNullException>(() =>
            LoggerFilterRuleSelector.Select(Array.Empty<LoggerFilterRule>(), null!, "X"));
        Assert.Throws<ArgumentException>(() =>
            LoggerFilterRuleSelector.Select(Array.Empty<LoggerFilterRule>(), typeof(RecordingProvider), ""));
        Assert.Throws<ArgumentNullException>(() =>
            LoggerFilterRuleSelector.Select(Array.Empty<LoggerFilterRule>(), typeof(RecordingProvider), null!));
    }
}
