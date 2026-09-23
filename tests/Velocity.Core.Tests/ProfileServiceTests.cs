using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Profiles;
using Velocity.Abstractions.Transactions;
using Velocity.Core.Games;
using Velocity.Core.Profiles;
using Velocity.Core.Transactions;
using Velocity.Data;
using Velocity.Data.Migrations;
using Velocity.Data.Repositories;
using Xunit;

namespace Velocity.Core.Tests;

/// <summary>
/// Covers profile storage, per-game resolution and the request a profile turns into.
/// </summary>
/// <remarks>
/// These run against a real SQLite database, because the behaviour worth testing is how stored
/// profiles and the built-in ones layer over each other, and a fake repository would test the fake.
/// </remarks>
public sealed class ProfileServiceTests : IAsyncLifetime
{
    private SqliteConnectionFactory _connectionFactory = null!;
    private ProfileService _service = null!;
    private ProfileRepository _repository = null!;
    private SettingsRepository _settings = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connectionFactory = SqliteConnectionFactory.CreateInMemory();
        var migrator = new DatabaseMigrator(_connectionFactory, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);

        _repository = new ProfileRepository(_connectionFactory);
        _settings = new SettingsRepository(_connectionFactory);
        _service = new ProfileService(_repository, _settings, NullLogger<ProfileService>.Instance);
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _connectionFactory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheBuiltInProfilesAreAlwaysPresent()
    {
        IReadOnlyList<OptimizationProfile> profiles =
            await _service.GetProfilesAsync(CancellationToken.None);

        Assert.Contains(profiles, profile => profile.Id == BuiltInProfiles.CompetitiveId);
        Assert.Contains(profiles, profile => profile.Id == BuiltInProfiles.MaximumFpsId);
        Assert.Contains(profiles, profile => profile.Id == BuiltInProfiles.BalancedId);
        Assert.All(profiles, profile => Assert.True(profile.IsBuiltIn));
    }

    [Fact]
    public async Task ABuiltInProfileCannotBeOverwrittenOrDeleted()
    {
        OptimizationProfile competitive = BuiltInProfiles.Competitive();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SaveAsync(competitive, CancellationToken.None));

        Assert.False(await _service.DeleteAsync(BuiltInProfiles.CompetitiveId, CancellationToken.None));
    }

    [Fact]
    public async Task AStoredProfileWithABuiltInIdDoesNotShadowTheBuiltInDefinition()
    {
        // A stale stored copy naming a module that no longer exists must never win.
        await _repository.UpsertAsync(
            BuiltInProfiles.Competitive() with
            {
                IsBuiltIn = false,
                Name = "Hijacked",
                Tweaks = [new ProfileTweakSetting("module.that.was.removed", true, new Dictionary<string, string>())],
            },
            CancellationToken.None);

        OptimizationProfile? resolved =
            await _service.GetProfileAsync(BuiltInProfiles.CompetitiveId, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("Competitive", resolved.Name);
        Assert.DoesNotContain(resolved.Tweaks, tweak => tweak.TweakId == "module.that.was.removed");
    }

    [Fact]
    public async Task AProfileBoundToAGameWinsOverTheDefault()
    {
        await _service.SetDefaultProfileAsync(BuiltInProfiles.BalancedId, CancellationToken.None);
        await _service.SaveAsync(UserProfile("per-game", gameId: "steam:1245620"), CancellationToken.None);

        OptimizationProfile? resolved =
            await _service.ResolveForGameAsync("steam:1245620", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("per-game", resolved.Id);
    }

    [Fact]
    public async Task AGameWithNoBoundProfileFallsBackToTheDefault()
    {
        await _service.SetDefaultProfileAsync(BuiltInProfiles.BalancedId, CancellationToken.None);

        OptimizationProfile? resolved =
            await _service.ResolveForGameAsync("steam:999", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(BuiltInProfiles.BalancedId, resolved.Id);
    }

    [Fact]
    public async Task WithNoDefaultConfiguredNothingIsResolvedRatherThanSomethingArbitrary()
    {
        // A session that resolves nothing runs unoptimized, which is correct. Picking a profile the
        // user never chose would change their machine without being asked.
        Assert.Null(await _service.ResolveForGameAsync("steam:1", CancellationToken.None));
    }

    [Fact]
    public async Task ClearingTheDefaultResolvesNothingAgain()
    {
        await _service.SetDefaultProfileAsync(BuiltInProfiles.BalancedId, CancellationToken.None);
        await _service.SetDefaultProfileAsync(null, CancellationToken.None);

        Assert.Null(await _service.ResolveForGameAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task AUserProfileRoundTripsThroughStorage()
    {
        OptimizationProfile saved = UserProfile("mine") with
        {
            Tweaks =
            [
                new ProfileTweakSetting(
                    "gpu.hardware-scheduling",
                    true,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["enabled"] = "false" }),
            ],
        };

        await _service.SaveAsync(saved, CancellationToken.None);

        OptimizationProfile? loaded = await _service.GetProfileAsync("mine", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("false", loaded.Tweaks.Single().Options["enabled"]);
    }

    [Fact]
    public void AProfileRequestIsNotAtomicSoOneUnsupportedModuleCostsNothingElse()
    {
        OptimizationRequest request = _service.BuildRequest(
            BuiltInProfiles.Competitive(), TransactionReason.ProfileApply, Guid.NewGuid());

        Assert.False(request.AtomicAllOrNothing);
        Assert.NotEmpty(request.TweakIds);
        Assert.Equal(BuiltInProfiles.CompetitiveId, request.ProfileId);
    }

    [Fact]
    public void ADisabledTweakIsNotRequested()
    {
        OptimizationProfile profile = UserProfile("mixed") with
        {
            Tweaks =
            [
                new ProfileTweakSetting("cpu.scheduler-quantum", true, new Dictionary<string, string>()),
                new ProfileTweakSetting("gpu.hardware-scheduling", false, new Dictionary<string, string>()),
            ],
        };

        OptimizationRequest request = _service.BuildRequest(profile, TransactionReason.ManualTweak);

        Assert.Equal(["cpu.scheduler-quantum"], request.TweakIds);
    }

    [Fact]
    public void TheBuiltInProfilesDifferInWhatTheyTradeNotInHowManyThingsTheyChange()
    {
        OptimizationProfile competitive = BuiltInProfiles.Competitive();
        OptimizationProfile maximumFps = BuiltInProfiles.MaximumFps();
        OptimizationProfile balanced = BuiltInProfiles.Balanced();

        // The competitive profile takes the modules that steady frame times; the FPS profile takes
        // the GPU scheduling change, which is a throughput bet worth measuring.
        Assert.Contains(competitive.Tweaks, tweak => tweak.TweakId == "network.interrupt-moderation");
        Assert.DoesNotContain(competitive.Tweaks, tweak => tweak.TweakId == "gpu.hardware-scheduling");
        Assert.Contains(maximumFps.Tweaks, tweak => tweak.TweakId == "gpu.hardware-scheduling");

        // Balanced changes nothing that survives a restart.
        Assert.DoesNotContain(balanced.Tweaks, tweak => tweak.TweakId == "gpu.hardware-scheduling");
        Assert.DoesNotContain(balanced.Tweaks, tweak => tweak.TweakId == "cpu.scheduler-quantum");
    }

    [Fact]
    public async Task DeclaredGamesSurviveAReadBackAndCanBeRemoved()
    {
        var declared = new UserDeclaredGames(_settings);

        await declared.DeclareAsync(@"D:\Emulators\MyEmulator.exe", isGame: true, CancellationToken.None);

        IReadOnlySet<string> stored =
            await declared.GetDeclaredExecutablesAsync(CancellationToken.None);

        // Only the file name is stored: a full path leaks the Windows user name often enough to
        // be worth not keeping.
        Assert.Equal(["MyEmulator.exe"], stored);
        Assert.Contains("myemulator.exe", stored);

        await declared.DeclareAsync("MyEmulator.exe", isGame: false, CancellationToken.None);
        Assert.Empty(await declared.GetDeclaredExecutablesAsync(CancellationToken.None));
    }

    private static OptimizationProfile UserProfile(string id, string? gameId = null) => new()
    {
        Id = id,
        Name = id,
        Kind = ProfileKind.Custom,
        GameId = gameId,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        ModifiedAtUtc = DateTimeOffset.UnixEpoch,
        Tweaks = [new ProfileTweakSetting("cpu.scheduler-quantum", true, new Dictionary<string, string>())],
    };
}
