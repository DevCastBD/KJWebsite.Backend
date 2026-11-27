using System;
using System.Linq;
using FluentAssertions;
using KJWebsite.BuildingBlocks.DataQuality;

namespace KJWebsite.BuildingBlocks.UnitTests;

public sealed class DataQualityTests
{
    [Theory]
    [InlineData("+8801712345678", "01712345678")]
    [InlineData("01712-345678", "+880 1712 345678")]
    public void PhonesInDifferentFormatsMeet(string a, string b) => DuplicateDetector.NormalisePhone(a).Should().Be(DuplicateDetector.NormalisePhone(b));

    [Fact]
    public void ShortNumbersAndSingleWordNamesAreNotEvidence()
    {
        DuplicateDetector.NormalisePhone("12345").Should().BeNull();
        DuplicateDetector.NormaliseName("Rahim").Should().BeNull();
        DuplicateDetector.NormaliseName("  Abdur   RAHIM. ").Should().Be("abdur rahim");
        DuplicateDetector.NormaliseName("আব্দুর রহিম").Should().Be("আব্দুর রহিম", "Bengali letters and marks are kept");
    }

    [Fact]
    public void ASharedEmailOrPhoneIsHighConfidenceAndTheSameNameAloneIsLow()
    {
        var found = DuplicateDetector.Detect(
        [
            new("a", "Abdur Rahim", "email-1", null),
            new("b", "A. Rahim", "email-1", null),
            new("c", "Nusrat Jahan", null, "+8801711000111"),
            new("d", "Nusrat J", null, "01711000111"),
            new("e", "Karim Uddin", null, null),
            new("f", "karim  uddin", null, null),
            new("g", "Unrelated Person", "email-9", "01999999999")
        ]);

        found.Select(c => (c.PairKey, c.Confidence)).Should().BeEquivalentTo([("a|b", "high"), ("c|d", "high"), ("e|f", "low")]);
        found.Single(c => c.PairKey == "a|b").Evidence.Select(e => e.Kind).Should().Equal("same_email");
        found.Should().OnlyContain(c => c.Evidence.All(e => !e.Detail.Contains("email-1") && !e.Detail.Contains("0171")), "evidence names the kind of match, never the value");
    }

    [Fact]
    public void ASharedEmailAndNameGivesBothPiecesOfEvidenceAndPairKeysAreOrderIndependent()
    {
        var found = DuplicateDetector.Detect([new("z", "Same Name", "k", null), new("y", "Same Name", "k", null)]);
        found.Should().ContainSingle().Which.Evidence.Select(e => e.Kind).Should().Equal("same_email", "same_name");
        DuplicateDetector.PairKey("a", "b").Should().Be(DuplicateDetector.PairKey("b", "a"));
    }

    [Fact]
    public void ABigSharedBucketIsCappedSoOnePhoneNumberCannotMakeAThousandPairs()
    {
        var records = Enumerable.Range(0, 200).Select(i => new PersonRecord($"id{i:000}", null, null, "01712345678"));
        DuplicateDetector.Detect(records).Count.Should().BeLessThanOrEqualTo(25 * 24 / 2);
    }

    [Fact]
    public void CompletenessCountsFilledAndMissingPerFieldAndWholeRecords()
    {
        var rows = new[] { ("m1", "x", "y"), ("m2", "x", ""), ("m3", "", "") };
        var report = Completeness.Build("member", rows, r => r.Item1, ("a", r => r.Item2.Length > 0), ("b", r => r.Item3.Length > 0));
        report.Total.Should().Be(3);
        report.CompleteRecords.Should().Be(1);
        report.CompletePercent.Should().Be("33.3");
        report.Fields.Single(f => f.Field == "a").Should().Match<FieldCompleteness>(f => f.Filled == 2 && f.Missing == 1 && f.FilledPercent == "66.7" && f.SampleMissingIds.SequenceEqual(new[] { "m3" }));
        report.Fields.Single(f => f.Field == "b").SampleMissingIds.Should().Equal("m2", "m3");
        Completeness.Build("none", Array.Empty<string>(), r => r, ("a", _ => false)).CompletePercent.Should().Be("100.0");
    }

    [Fact]
    public void DecisionsNeedTwoDifferentRecordsAKnownDecisionAndANote()
    {
        DuplicateDecisions.Validate(new("a", "b", "not_duplicate", "Different people, checked ids")).Should().BeNull();
        DuplicateDecisions.Validate(new("a", "a", "not_duplicate", "Different people, checked ids")).Should().NotBeNull();
        DuplicateDecisions.Validate(new("a", "b", "merge", "Different people, checked ids")).Should().NotBeNull();
        DuplicateDecisions.Validate(new("a", "b", "confirmed_duplicate", "no")).Should().NotBeNull();
        DuplicateDecisions.Validate(new(null, "b", "confirmed_duplicate", "long enough note")).Should().NotBeNull();
    }
}
