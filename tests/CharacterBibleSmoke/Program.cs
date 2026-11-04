using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + description);
    Console.WriteLine("PASS: " + description);
}

Check(!new ProjectOptions().EnableCharacterConsistency,
    "New projects keep Character Bible disabled by default");

var root = Path.Combine(Path.GetTempPath(),
    "ZeroVideoCharacterSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var emptyTrailer = new VideoProject
    {
        Id = "trailer-test",
        ProjectDirectory = root,
        Options = new ProjectOptions { EnableCharacterConsistency = true },
        Chapters =
        [
            new ProjectChapter
            {
                Number = 1,
                Scenes =
                [
                    new ProjectScene
                    {
                        Number = 1,
                        Narration = "The Desert Trailer stood empty in the valley.",
                        ImagePrompt = "A deserted trailer parked in a dry empty landscape.",
                        VisualQuery = "Exterior RV establishing view with no people"
                    },
                    new ProjectScene
                    {
                        Number = 2,
                        Narration = "The Desert Trailer remained abandoned.",
                        ImagePrompt = "An unoccupied caravan with scrubby grass.",
                        VisualQuery = "Landscape and mountains, no characters"
                    }
                ]
            }
        ]
    };

    CharacterConsistencyService.EnsureProfiles(emptyTrailer);
    Check(emptyTrailer.Characters.Count == 0,
        "Repeated trailer or landscape names are not recognized as people");

    var person = new VideoProject
    {
        Id = "person-test",
        ProjectDirectory = root,
        Options = new ProjectOptions { EnableCharacterConsistency = true },
        Chapters =
        [
            new ProjectChapter
            {
                Number = 1,
                Scenes =
                [
                    new ProjectScene
                    {
                        Number = 1,
                        Narration = "Ted Bundy entered a courtroom with his lawyer.",
                        ImagePrompt = "An illustrated portrait of Ted Bundy in a courtroom."
                    },
                    new ProjectScene
                    {
                        Number = 2,
                        Narration = "Ted Bundy was questioned by police officers.",
                        ImagePrompt = "Ted Bundy wearing a dark jacket; close face view."
                    },
                    new ProjectScene
                    {
                        Number = 3,
                        Narration = "Ted Bundy had passed by a remote trailer.",
                        ImagePrompt = "An empty trailer exterior in the desert, no people.",
                        VisualQuery = "A deserted vehicle with no characters"
                    }
                ]
            }
        ]
    };
    CharacterConsistencyService.EnsureProfiles(person);
    var profile = person.Characters.Single(c => c.Name == "Ted Bundy");

    var shots = person.Chapters[0].Scenes;
    Check(CharacterConsistencyService.Match(person, shots[0]).Count == 1,
        "Character guidance applies to a named visible person");
    Check(CharacterConsistencyService.Match(person, shots[1]).Count == 1,
        "Identity guidance is retained in a separate human portrait shot");
    Check(CharacterConsistencyService.Match(person, shots[2]).Count == 0,
        "Mentioning a person over an empty trailer does not lock the trailer");
    Check(!CharacterConsistencyService.IdentityDescription(profile)
        .Contains("courtroom", StringComparison.OrdinalIgnoreCase),
        "Auto-detected character description does not embed the first scene");

    profile.Appearance =
        "Keep the same recognizable face, hair, apparent age, wardrobe, " +
        "accessories and distinctive features in every scene. " +
        "Always replicate an old RV and exact same lighting.";
    Check(!CharacterConsistencyService.IdentityDescription(profile)
        .Contains("RV", StringComparison.OrdinalIgnoreCase),
        "Legacy scene directions are removed from auto profile appearance");

    var folder = CharacterConsistencyService.AnchorDirectory(person);
    Directory.CreateDirectory(folder);
    var old = Path.Combine(folder, "Ted_Bundy-" + profile.Seed + ".png");
    File.WriteAllBytes(old, [1,2,3,4,5,6]);
    profile.ReferenceImagePath = old;
    Check(CharacterConsistencyService.GetExistingReference(person, profile) is null,
        "Legacy generated whole-scene anchors cannot be reused");

    var imported = Path.Combine(folder, "user-Ted_Bundy-test.png");
    File.WriteAllBytes(imported, [1,2,3,4,5,6]);
    profile.ReferenceImagePath = imported;
    Check(CharacterConsistencyService.GetExistingReference(person, profile) == imported,
        "Explicitly imported character portraits remain supported");

    person.Options.EnableCharacterConsistency = false;
    Check(CharacterConsistencyService.Match(person, shots[0]).Count == 0,
        "Project toggle disables character guidance immediately");

    var seed1 = CharacterConsistencyService.StableSeed("person-test|shot|1|1|" + profile.Seed);
    var seed2 = CharacterConsistencyService.StableSeed("person-test|shot|1|2|" + profile.Seed);
    Check(seed1 != seed2, "Different scenes use different deterministic seeds");

    // Real person identity should affect only explicitly visible subjects.
    Check(!new ProjectOptions().EnableRealPersonIdentityLock,
        "Real Person Identity Lock defaults OFF for new projects");
    person.RealPeople =
    [
        new RealPersonIdentity { Name = "Ted Bundy", Enabled = true },
        new RealPersonIdentity { Name = "Zak Bagans", Enabled = true }
    ];
    person.Options.EnableRealPersonIdentityLock = true;
    Check(RealPersonIdentityService.Match(person, shots[0])?.Name == "Ted Bundy",
        "The named visible courtroom subject matches a real-person profile");
    Check(RealPersonIdentityService.Match(person, shots[1])?.Name == "Ted Bundy",
        "Named portrait identity persists across independent compositions");
    Check(RealPersonIdentityService.Match(person, shots[2]) is null,
        "No real-person identity on empty trailer even with named narration");
    var namedNoRole = new ProjectScene
    {
        ImagePrompt = "Zak Bagans in a candlelit hallway, cinematic comic.",
        Narration = "Zak Bagans visits the building."
    };
    Check(RealPersonIdentityService.Match(person, namedNoRole)?.Name == "Zak Bagans",
        "Explicit name in visual prompt is a visible-person cue");
    var evidence = new ProjectScene
    {
        ImagePrompt = "Evidence-only close shot, no visible person.",
        Narration = "Jeffrey Dahmer was discussed in the report."
    };
    person.RealPeople.Add(new RealPersonIdentity { Name="Jeffrey Dahmer" });
    Check(RealPersonIdentityService.Match(person, evidence) is null,
        "No person selected for an object-only evidence cutaway");
    var twoPeople = new ProjectScene
    {
        ImagePrompt = "Zak Bagans with Ted Bundy, two characters in a collage.",
        Narration = "Zak Bagans and Ted Bundy"
    };
    Check(RealPersonIdentityService.Match(person, twoPeople) is null,
        "Ambiguous two-person scenes never assign a wrong face");
    person.Options.EnableRealPersonIdentityLock = false;
    Check(RealPersonIdentityService.Match(person, shots[0]) is null,
        "Identity Lock OFF disables all identity conditioning immediately");

    // Hands-free per-topic identity: no prompt for each video.
    Check(new ProjectOptions().AutoRealPersonIdentity,
        "New projects automatically enable per-topic real-person mode");
    Check(new ProjectOptions().AutoIdentityPortraitCount == 2,
        "Default automatic identity uses two temporary portrait guides");

    var zak = new VideoProject
    {
        Id = "zak-project",
        ProjectDirectory = Path.Combine(root, "zak-project"),
        Title = "Zak Bagans — The Ghost Hunter Who Built an Empire",
        Topic = "zak bagans story"
    };
    var dahmer = new VideoProject
    {
        Id = "dahmer-project",
        ProjectDirectory = Path.Combine(root, "dahmer-project"),
        Title = "Jeffrey Dahmer — The Investigation",
        Topic = "Jeffrey Dahmer story"
    };
    var unknown = new VideoProject
    {
        Title = "The Zodiac Killer — America's Unsolved Cipher",
        Topic = "The Axeman of New Orleans"
    };
    Check(AutoRealPersonIdentityService.DetectMainPerson(zak) == "Zak Bagans",
        "Zak Bagans is detected from the current video title");
    Check(AutoRealPersonIdentityService.DetectMainPerson(dahmer) ==
        "Jeffrey Dahmer", "Next topic detects Jeffrey Dahmer independently");
    Check(AutoRealPersonIdentityService.DetectMainPerson(unknown) is null,
        "Unnamed killer/case titles do not trigger a fabricated identity");

    var zakProfile=AutoRealPersonIdentityService.GetOrCreate(zak,"Zak Bagans");
    Check(zakProfile.AutoCreatedForVideo,
        "Automatically created profile is marked separately from manual profiles");
    var zakTemp=AutoRealPersonIdentityService.TemporaryDirectory(zak);
    var dahmerTemp=AutoRealPersonIdentityService.TemporaryDirectory(dahmer);
    Directory.CreateDirectory(zakTemp);
    Directory.CreateDirectory(dahmerTemp);
    var zakPhoto=Path.Combine(zakTemp,"AI-APPROXIMATE-Zak_Bagans-v1-a123.png");
    var dahmerPhoto=Path.Combine(dahmerTemp,
        "AI-APPROXIMATE-Jeffrey_Dahmer-v1-b456.png");
    File.WriteAllBytes(zakPhoto,new byte[2048]);
    File.WriteAllBytes(dahmerPhoto,new byte[2048]);
    AutoRealPersonIdentityService.AttachTemporary(zak,zakProfile,zakPhoto);
    var dahmerProfile=AutoRealPersonIdentityService.GetOrCreate(
        dahmer,"Jeffrey Dahmer");
    AutoRealPersonIdentityService.AttachTemporary(dahmer,dahmerProfile,dahmerPhoto);
    Check(AutoRealPersonIdentityService.ValidTemporaryCount(zak,zakProfile)==1,
        "Interrupted project retains one checkpointed temporary portrait");
    var manualPhoto=Path.Combine(zak.ProjectDirectory,"approved-zak.png");
    File.WriteAllBytes(manualPhoto,new byte[2048]);
    zakProfile.ReferencePaths.Add(manualPhoto);

    // A path outside the tracked project-specific temporary folder cannot
    // be registered for automatic deletion.
    var outsideRejected=false;
    try
    {
        AutoRealPersonIdentityService.AttachTemporary(zak,zakProfile,manualPhoto);
    }
    catch(InvalidDataException){ outsideRejected=true; }
    Check(outsideRejected,
        "A manually approved photo cannot be tagged as a disposable temp");
    Check(!AutoRealPersonIdentityService.IsSafeTemporaryPath(zak,dahmerPhoto),
        "Another video's temporary portrait is outside this project's deletion scope");

    zak.Options.EnableRealPersonIdentityLock=true;
    zak.AutoIdentityEnabledByVideo=true;
    zak.ActiveAutoIdentityPerson="Zak Bagans";
    var report=AutoRealPersonIdentityService.Cleanup(zak);
    Check(report.Deleted==1 && report.Failed==0,
        "Successful-video cleanup removes exactly the tracked temporary portrait");
    Check(!File.Exists(zakPhoto) && File.Exists(manualPhoto),
        "Cleanup deletes only auto-generated references, never manual photos");
    Check(File.Exists(dahmerPhoto),
        "Completing Zak project does not delete Dahmer project portraits");
    Check(!zak.Options.EnableRealPersonIdentityLock &&
        string.IsNullOrEmpty(zak.ActiveAutoIdentityPerson),
        "Auto-activated identity is cleared after video completion");
    Check(zak.RealPeople.Count==1 &&
        zak.RealPeople[0].ReferencePaths.Count==1 &&
        zak.RealPeople[0].ReferencePaths[0]==manualPhoto,
        "Manually supplied references are kept intact and attached");

    // The next person's completed video should clean only its own files.
    dahmer.Options.EnableRealPersonIdentityLock=true;
    dahmer.AutoIdentityEnabledByVideo=true;
    dahmer.ActiveAutoIdentityPerson="Jeffrey Dahmer";
    var nextReport=AutoRealPersonIdentityService.Cleanup(dahmer);
    Check(nextReport.Deleted==1 && dahmer.RealPeople.Count==0,
        "Next-topic references are removed after its completed video");
    Check(File.Exists(manualPhoto),
        "Next-topic cleanup cannot delete an earlier project's manual source");

    Console.WriteLine("Character Bible regression checks passed.");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
