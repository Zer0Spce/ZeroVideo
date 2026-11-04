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

    Console.WriteLine("Character Bible regression checks passed.");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
