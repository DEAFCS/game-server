using System.Text.Json;
using FiveStack.Entities.Practice;
using FiveStack.Utilities;
using Xunit;

namespace FiveStack.Tests;

// DEAFCS: a practice server is told where the DEAFCS inventory lives with its
// session, as a match server is by the Competitive cfg it execs. Placeholder
// values only: no real service or key appears in a test.
public class PracticeInventoryTests
{
    private static PracticeServerSetting Setting(string? name, string? value) =>
        new() { name = name, value = value };

    [Fact]
    public void TheSessionBodyCarriesTheSettings()
    {
        const string body =
            "{\"session_id\":\"00000000-0000-0000-0000-000000000001\",\"password\":\"p\","
            + "\"inventory_settings\":[{\"name\":\"invsim_url\",\"value\":\"https://inventory.example.test\"},"
            + "{\"name\":\"invsim_apikey\",\"value\":\"test-key\"}]}";

        PracticeSessionData session = JsonSerializer
            .Deserialize<UtilitySessionRow>(body, PracticeJson.Options)!
            .ToSession();

        Assert.Equal(2, session.inventory_settings.Count);
        Assert.Equal("invsim_url", session.inventory_settings[0].name);
    }

    [Fact]
    public void APanelThatSendsNoneLeavesNothingToApply()
    {
        PracticeSessionData session = JsonSerializer
            .Deserialize<UtilitySessionRow>("{\"password\":\"p\"}", PracticeJson.Options)!
            .ToSession();

        Assert.Empty(session.inventory_settings);
        Assert.Empty(PracticeInventory.Commands(session.inventory_settings));
    }

    [Fact]
    public void SettingsBecomeQuotedConsoleCommandsInOrder()
    {
        var commands = PracticeInventory.Commands(new[]
        {
            Setting("invsim_url", "https://inventory.example.test"),
            Setting("INVSIM_WS_ENABLED", "1"),
            Setting("invsim_require_inventory", "1"),
        });

        Assert.Equal(
            new[]
            {
                "invsim_url \"https://inventory.example.test\"",
                "invsim_ws_enabled \"1\"",
                "invsim_require_inventory \"1\"",
            },
            commands
        );
    }

    [Theory]
    [InlineData("sv_password", "x")]
    [InlineData("rcon_password", "x")]
    [InlineData("invsim url", "x")]
    [InlineData("xinvsim_url", "x")]
    [InlineData("invsim_url", "a;quit")]
    [InlineData("invsim_url", "a\"; quit \"")]
    [InlineData("invsim_url", "a\nquit")]
    [InlineData(null, "x")]
    [InlineData("invsim_url", null)]
    public void NothingButAPlainInventorySettingIsRun(string? name, string? value)
    {
        Assert.Empty(PracticeInventory.Commands(new[] { Setting(name, value) }));
    }

    [Fact]
    public void AnOverlongValueIsDropped()
    {
        Assert.Empty(
            PracticeInventory.Commands(new[]
            {
                Setting("invsim_url", new string('a', PracticeInventory.MaxValueLength + 1)),
            })
        );
    }

    [Fact]
    public void TheLogSeesNamesNeverValues()
    {
        var names = PracticeInventory.Names(new[]
        {
            Setting("invsim_url", "https://inventory.example.test"),
            Setting("invsim_apikey", "test-key"),
            Setting("sv_password", "x"),
        });

        Assert.Equal(new[] { "invsim_url", "invsim_apikey" }, names);
        Assert.DoesNotContain(names, name => name.Contains("test-key"));
    }
}
