using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// The published error vocabulary is a CONTRACT: clients switch on it and
/// decide whether to retry, re-resolve, or stop. These tests pin it to the
/// enum so it cannot drift silently again.
///
/// History: `errors` was two hand-maintained string literals (one per
/// capability profile). They had drifted from <see cref="OutcomeKind"/> in
/// both directions — six real outcomes were never published, and one name was
/// published that no client could ever observe. Nothing failed loudly.
/// </summary>
public sealed class ErrorVocabularyTests
{
    /// <summary>Outcomes published separately under `verification` — an action
    /// that returns Verified did not return an error, so listing it here
    /// would tell clients to treat success as failure.</summary>
    private static readonly string[] VerificationOnly =
        ["Verified", "Unverified", "Failed", "ObservedChange"];

    private static string[] PublishedErrors() =>
        JsonSerializer
            .Deserialize<JsonElement>(InbriskResources.CapabilitiesJson())
            .GetProperty("errors")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

    [Fact]
    public void PublishedErrorsAreAllRealOutcomeKinds()
    {
        var defined = Enum.GetNames<OutcomeKind>().ToHashSet(StringComparer.Ordinal);

        foreach (var name in PublishedErrors())
            Assert.True(defined.Contains(name),
                $"Yayınlanan '{name}' OutcomeKind enumunda tanımsız — "
                + "istemciler bunu bekler ama sunucu asla döndüremez.");
    }

    [Fact]
    public void EveryErrorOutcomeIsPublished()
    {
        var published = PublishedErrors().ToHashSet(StringComparer.Ordinal);

        foreach (var name in Enum.GetNames<OutcomeKind>())
        {
            if (VerificationOnly.Contains(name)) continue;
            Assert.True(published.Contains(name),
                $"'{name}' enumda var ama yayınlanmadı — istemci onu "
                + "tanımıyor ve doğru kurtarma stratejisini seçemiyor.");
        }
    }

    [Fact]
    public void VerificationOutcomesAreNotPublishedAsErrors()
    {
        var published = PublishedErrors().ToHashSet(StringComparer.Ordinal);

        foreach (var name in VerificationOnly)
            Assert.False(published.Contains(name),
                $"'{name}' bir doğrulama sonucu, hata değil; `verification` "
                + "altında yayınlanıyor ve `errors` içinde yer almamalı.");
    }

    [Fact]
    public void BothCapabilityProfilesPublishTheSameVocabulary()
    {
        // full and ambient are separate literals today; deriving must keep
        // them identical so a client can cache one and reuse it.
        var full = InbriskResources.CapabilitiesJson();
        var ambient = InbriskResources.CapabilitiesJsonSlim();

        static string[] Err(string json) =>
            JsonSerializer.Deserialize<JsonElement>(json)
                .GetProperty("errors")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToArray();

        Assert.Equal(Err(full), Err(ambient));
    }
}