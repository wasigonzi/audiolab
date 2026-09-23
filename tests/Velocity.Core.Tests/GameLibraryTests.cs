using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Abstractions.Games;
using Velocity.Abstractions.Processes;
using Velocity.Core.Games;
using Velocity.TestSupport;
using Xunit;

namespace Velocity.Core.Tests;

/// <summary>
/// Covers store manifest parsing, library aggregation and running game detection.
/// </summary>
/// <remarks>
/// The manifest text in these fixtures is the real on-disk shape, so the parsers are exercised
/// against what Steam and Epic actually write rather than against a convenient simplification.
/// </remarks>
public sealed class GameLibraryTests
{
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";
    private const string SecondLibrary = @"D:\SteamLibrary";

    [Fact]
    public void TheKeyValueParserReadsNestedBlocksAndEscapes()
    {
        KeyValueNode document = ValveKeyValueParser.Parse(
            """
            "AppState"
            {
                // a comment
                "appid"    "271590"
                "name"     "Grand Theft Auto \"V\""
                "UserConfig"
                {
                    "language"  "english"
                }
            }
            """);

        Assert.Equal("271590", document.ValueAt("AppState", "appid"));
        Assert.Equal("Grand Theft Auto \"V\"", document.ValueAt("AppState", "name"));
        Assert.Equal("english", document.ValueAt("AppState", "UserConfig", "language"));
    }

    [Fact]
    public void AnUnterminatedManifestYieldsWhatParsedRatherThanThrowing()
    {
        // One corrupt manifest must not cost the user the rest of their library.
        KeyValueNode document = ValveKeyValueParser.Parse(
            """
            "AppState"
            {
                "appid"  "12345"
                "name"   "Half written
            """);

        Assert.Equal("12345", document.ValueAt("AppState", "appid"));
    }

    [Fact]
    public void TheParserDoesNotRecurseWithoutBound()
    {
        // A file of nothing but opening braces must terminate rather than overflow the stack.
        KeyValueNode document = ValveKeyValueParser.Parse(string.Concat(Enumerable.Repeat("\"a\"{", 500)));
        Assert.NotNull(document);
    }

    [Fact]
    public void BothLibraryFolderLayoutsAreRead()
    {
        IReadOnlyList<string> modern = SteamLibrarySource.ParseLibraryFolders(ValveKeyValueParser.Parse(
            """
            "libraryfolders"
            {
                "0"
                {
                    "path"  "C:\\Program Files (x86)\\Steam"
                }
                "1"
                {
                    "path"  "D:\\SteamLibrary"
                }
            }
            """));

        IReadOnlyList<string> legacy = SteamLibrarySource.ParseLibraryFolders(ValveKeyValueParser.Parse(
            """
            "LibraryFolders"
            {
                "TimeNextStatsReport"  "0"
                "1"                    "D:\\SteamLibrary"
            }
            """));

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], modern);
        Assert.Equal([@"D:\SteamLibrary"], legacy);

        // "TimeNextStatsReport" is not an index and must not become a library path.
        Assert.DoesNotContain(legacy, path => path == "0");
    }

    [Fact]
    public async Task SteamGamesAreFoundAcrossEveryLibraryFolder()
    {
        FakeGameFileSystem files = SteamFixture();

        IReadOnlyList<GameInstallation> games = await CreateSteamSource(files)
            .ScanAsync(CancellationToken.None);

        Assert.Equal(2, games.Count);
        Assert.Contains(games, game => game.Name == "Counter-Strike 2");
        Assert.Contains(games, game => game.Name == "Elden Ring");
    }

    [Fact]
    public async Task ASteamGameIsLaunchedThroughSteamRatherThanAGuessedExecutable()
    {
        GameInstallation game = (await CreateSteamSource(SteamFixture()).ScanAsync(CancellationToken.None))
            .Single(entry => entry.Name == "Elden Ring");

        Assert.Equal("steam://rungameid/1245620", game.LaunchCommand);

        // Steam's manifest does not name the binary. Guessing one picks the anti-cheat service as
        // often as the game, so the module leaves it unknown.
        Assert.Null(game.ExecutablePath);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\ELDEN RING", game.InstallDirectory);
    }

    [Fact]
    public async Task ASteamManifestWithoutAnAppIdIsSkipped()
    {
        var files = new FakeGameFileSystem()
            .AddFile($@"{SteamRoot}\steamapps\appmanifest_0.acf", "\"AppState\" { \"name\" \"Broken\" }");

        Assert.Empty(await CreateSteamSource(files).ScanAsync(CancellationToken.None));
    }

    [Fact]
    public void AnEpicManifestNamesItsExecutable()
    {
        GameInstallation? game = EpicLibrarySource.ParseManifest(
            """
            {
              "AppName": "Fortnite",
              "DisplayName": "Fortnite",
              "InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite",
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe",
              "InstallSize": 91234567890,
              "CatalogNamespace": "fn",
              "CatalogItemId": "4fe75bbc5a674f4f9b356b5c90567da5"
            }
            """,
            new FakeGameFileSystem());

        Assert.NotNull(game);
        Assert.Equal("epic:Fortnite", game.Id);
        Assert.Equal(
            @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe",
            game.ExecutablePath);
        Assert.Equal(91234567890L, game.SizeBytes);
        Assert.StartsWith("com.epicgames.launcher://apps/fn%3A", game.LaunchCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEpicManifestMissingItsCatalogIdsGetsNoLaunchUrl()
    {
        // A partial URL would silently do nothing, so the caller falls back to the executable.
        GameInstallation? game = EpicLibrarySource.ParseManifest(
            """
            {
              "AppName": "Thing",
              "DisplayName": "Thing",
              "InstallLocation": "C:\\Games\\Thing",
              "LaunchExecutable": "Thing.exe"
            }
            """,
            new FakeGameFileSystem());

        Assert.NotNull(game);
        Assert.Null(game.LaunchCommand);
        Assert.Equal(@"C:\Games\Thing\Thing.exe", game.ExecutablePath);
    }

    [Fact]
    public void MalformedEpicJsonIsSkippedRatherThanThrowing()
    {
        Assert.Null(EpicLibrarySource.ParseManifest("{ not json", new FakeGameFileSystem()));
        Assert.Null(EpicLibrarySource.ParseManifest("[]", new FakeGameFileSystem()));
        Assert.Null(EpicLibrarySource.ParseManifest(null, new FakeGameFileSystem()));
    }

    [Fact]
    public async Task AFailingSourceIsReportedRatherThanLosingTheOtherStores()
    {
        var library = new GameLibrary(
            [
                new FakeGameLibrarySource(GameStore.Steam, [Game("steam:1", "Playable")]),
                new FakeGameLibrarySource(
                    GameStore.Epic, [], new UnauthorizedAccessException("Manifests are not readable.")),
            ],
            TimeProvider.System,
            NullLogger<GameLibrary>.Instance);

        GameLibraryScan scan = await library.ScanAsync(CancellationToken.None);

        Assert.Single(scan.Games);
        Assert.Equal("Manifests are not readable.", scan.FailedSources["Epic"]);
    }

    [Fact]
    public async Task ASecondQueryDoesNotRescanTheDisk()
    {
        var source = new CountingSource();
        var library = new GameLibrary([source], TimeProvider.System, NullLogger<GameLibrary>.Instance);

        await library.GetAsync(CancellationToken.None);
        await library.GetAsync(CancellationToken.None);

        // Re-reading every manifest during a game is disk I/O the product must not add.
        Assert.Equal(1, source.ScanCount);
    }

    [Fact]
    public void AnExactExecutableMatchIsTheStrongestEvidence()
    {
        GameInstallation game = Game("epic:x", "Thing", executable: @"C:\Games\Thing\Thing.exe");

        DetectedGame? detected = GameDetector.Match(
            Process(10, "Thing.exe", @"C:\Games\Thing\Thing.exe"),
            [game],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.NotNull(detected);
        Assert.Equal(GameDetectionReason.InstalledLibraryMatch, detected.Reason);
        Assert.Equal(game, detected.Game);
    }

    [Fact]
    public void LivingInsideAGameFolderIsReportedAsTheWeakerReason()
    {
        GameInstallation game = Game("steam:1", "Thing", installDirectory: @"C:\Games\Thing");

        DetectedGame? detected = GameDetector.Match(
            Process(11, "ThingCrashHandler.exe", @"C:\Games\Thing\bin\ThingCrashHandler.exe"),
            [game],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.NotNull(detected);
        Assert.Equal(GameDetectionReason.StoreLibraryFolder, detected.Reason);
    }

    [Fact]
    public void AnUnrelatedProcessIsNotGuessedToBeAGame()
    {
        // No GPU heuristic, no window style guess: a video call is not a game.
        Assert.Null(GameDetector.Match(
            Process(12, "Teams.exe", @"C:\Program Files\Teams\Teams.exe"),
            [Game("steam:1", "Thing", installDirectory: @"C:\Games\Thing")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AProtectedProcessIsNeverAGameEvenInsideAGameFolder()
    {
        ProcessSnapshot agent = Process(13, "SecurityAgent.exe", @"C:\Games\Thing\SecurityAgent.exe")
            with { Protection = ProcessProtection.Protected };

        Assert.Null(GameDetector.Match(
            agent,
            [Game("steam:1", "Thing", installDirectory: @"C:\Games\Thing")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AUserDeclaredExecutableIsDetectedWithoutALibraryEntry()
    {
        DetectedGame? detected = GameDetector.Match(
            Process(14, "MyEmulator.exe", @"D:\Emulators\MyEmulator.exe"),
            [],
            new HashSet<string>(["myemulator.exe"], StringComparer.OrdinalIgnoreCase));

        Assert.NotNull(detected);
        Assert.Equal(GameDetectionReason.UserDeclared, detected.Reason);
        Assert.Null(detected.Game);
    }

    [Fact]
    public async Task DetectionReturnsStrongerEvidenceFirst()
    {
        GameInstallation exact = Game("epic:x", "Exact", executable: @"C:\Games\Exact\Exact.exe");
        GameInstallation folder = Game("steam:1", "Folder", installDirectory: @"C:\Games\Folder");

        var detector = new GameDetector(
            new FakeProcessInspector(
            [
                Process(20, "Helper.exe", @"C:\Games\Folder\Helper.exe"),
                Process(21, "Exact.exe", @"C:\Games\Exact\Exact.exe"),
            ]),
            new FakeGameLibrary(new GameLibraryScan([exact, folder], new Dictionary<string, string>())),
            new FakeUserDeclaredGames(),
            NullLogger<GameDetector>.Instance);

        IReadOnlyList<DetectedGame> detected = await detector.DetectRunningGamesAsync(CancellationToken.None);

        Assert.Equal(2, detected.Count);
        Assert.Equal(GameDetectionReason.InstalledLibraryMatch, detected[0].Reason);
    }

    [Fact]
    public void AStoreIdentifierDoesNotChangeWhenTheTitleIsRenamed()
    {
        Assert.Equal(
            GameIdentity.ForStoreApp(GameStore.Steam, "1245620"),
            GameIdentity.ForStoreApp(GameStore.Steam, "1245620"));

        Assert.NotEqual(
            GameIdentity.ForStoreApp(GameStore.Steam, "1"),
            GameIdentity.ForStoreApp(GameStore.Epic, "1"));
    }

    [Fact]
    public void AnExecutableIdentifierHashesThePathRatherThanStoringIt()
    {
        string id = GameIdentity.ForExecutable(@"C:\Users\someone\Games\Thing.exe");

        Assert.StartsWith("exe:", id, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", id, StringComparison.OrdinalIgnoreCase);

        // Case and separator differences describe the same file.
        Assert.Equal(id, GameIdentity.ForExecutable(@"c:/Users/Someone/Games/Thing.exe"));
    }

    private static FakeGameFileSystem SteamFixture() => new FakeGameFileSystem()
        .AddFile(
            $@"{SteamRoot}\steamapps\libraryfolders.vdf",
            """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """)
        .AddFile(
            $@"{SteamRoot}\steamapps\appmanifest_730.acf",
            """
            "AppState"
            {
                "appid"       "730"
                "name"        "Counter-Strike 2"
                "installdir"  "Counter-Strike Global Offensive"
                "SizeOnDisk"  "34359738368"
            }
            """)
        .AddFile(
            $@"{SecondLibrary}\steamapps\appmanifest_1245620.acf",
            """
            "AppState"
            {
                "appid"       "1245620"
                "name"        "Elden Ring"
                "installdir"  "ELDEN RING"
                "SizeOnDisk"  "64424509440"
            }
            """);

    private static SteamLibrarySource CreateSteamSource(FakeGameFileSystem files) =>
        new(new FakeStoreLocator().WithRoots(GameStore.Steam, SteamRoot),
            files,
            NullLogger<SteamLibrarySource>.Instance);

    private static GameInstallation Game(
        string id,
        string name,
        string? executable = null,
        string? installDirectory = null) => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = executable,
        InstallDirectory = installDirectory,
    };

    private static ProcessSnapshot Process(int id, string name, string path) => new()
    {
        ProcessId = id,
        ExecutableName = name,
        ExecutablePath = path,
    };

    private sealed class CountingSource : IGameLibrarySource
    {
        public int ScanCount { get; private set; }

        public GameStore Store => GameStore.Steam;

        public Task<IReadOnlyList<GameInstallation>> ScanAsync(CancellationToken cancellationToken)
        {
            ScanCount++;
            return Task.FromResult<IReadOnlyList<GameInstallation>>([]);
        }
    }
}
