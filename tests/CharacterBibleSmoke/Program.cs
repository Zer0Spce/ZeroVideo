using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + description);
    Console.WriteLine("PASS: " + description);
}

const string oldChatJson = """{"number":1,"scenes":[{"narration":"old"}]}""";
const string newChatJson = """{"number":2,"scenes":[{"narration":"new"}]}""";
Check(ChatGptResponseRecovery.FindFreshJson(
    [oldChatJson, newChatJson], 1, oldChatJson, oldChatJson) == newChatJson,
    "ChatGPT recovery selects new chapter JSON, never the prior completed response");
Check(ChatGptResponseRecovery.FindFreshJson(
    [oldChatJson, """{"number":2,"scenes":[ """], 1, oldChatJson, oldChatJson) is null,
    "ChatGPT recovery never accepts old JSON while a new reply is incomplete");
Check(ChatGptResponseRecovery.FindFreshJson(
    [oldChatJson], 1, oldChatJson, oldChatJson) is null,
    "ChatGPT recovery does not mistake an unchanged prior turn for a new one");
Check(ChatGptResponseRecovery.FindFreshJson(
    [oldChatJson, newChatJson], 2, oldChatJson, oldChatJson) == newChatJson,
    "ChatGPT recovery handles a DOM-reused response with no new block");
Check(ChatGptResponseRecovery.ExtractLastValidJsonObject(
    "partial prefix\n" + newChatJson + "\nfinal text") == newChatJson,
    "ChatGPT recovery extracts an entire response through wrapper text");
Check(ChatGptResponseRecovery.FindFreshJson(
    [oldChatJson, "Temporary ChatGPT error"], 1, oldChatJson, oldChatJson) is null,
    "ChatGPT recovery will not use stale JSON when the newest response is prose");

// Regression: ChatGPT's newer rendered UI has zero author-role, assistant,
// conversation-turn and .markdown selectors, yet main.innerText contains JSON.
// These tests exercise the exact fallback used in that situation.
const string previousOutline = """{"chapters":[{"number":1,"title":"Old case"}]}""";
const string newOutline = """{"chapters":[{"number":1,"title":"Rader"},{"number":2,"title":"Evidence"}]}""";
const string outlinePrompt = "Create ONLY the master outline. Required JSON schema: " +
    """{"chapters":[]}""";
const string pageBeforeSend = "Older conversation\n" + previousOutline +
    "\nChatGPT can make mistakes. Check important info.";
const string mainAfterResponse = "Older conversation\n" + previousOutline +
    "\nCreate Master Outline\n" + outlinePrompt +
    "\nLatest response\n" + newOutline +
    "\nChatGPT can make mistakes. Check important info.\nThinking effort\nMedium";
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    mainAfterResponse, pageBeforeSend, outlinePrompt) == newOutline,
    "Plain-main-text fallback recovers completed outline with ZERO assistant DOM markers");
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    pageBeforeSend, pageBeforeSend, outlinePrompt) is null,
    "Plain-main-text fallback rejects an unchanged old conversation");
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    pageBeforeSend + "\n" + outlinePrompt, pageBeforeSend, outlinePrompt) is null,
    "Plain-main-text fallback cannot mistake a prompt JSON schema for a response");
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    pageBeforeSend + "\n" + outlinePrompt +
    "\nLatest response\n" + """{"chapters":[{"number":1,"title":"Still typing"}""" ,
    pageBeforeSend, outlinePrompt) is null,
    "Plain-main-text fallback does not reuse old JSON while assistant is streaming");
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    "Prior transcript\n" + new string('x', 60000) + "\n" + newOutline +
    "\nChatGPT can make mistakes. Check important info.",
    pageBeforeSend, outlinePrompt) == newOutline,
    "Plain-main-text fallback reads a long 60k-character conversation");
const string chapterPrompt = "You are writing ONE chapter. Return scenes in valid JSON.";
const string chapterJson = """{"number":1,"title":"The Disk","scenes":[{"number":1,"narration":"Evidence."}]}""";
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    "History\n" + chapterPrompt + "\n" + chapterJson,
    "History", chapterPrompt) == chapterJson,
    "Plain-main-text fallback recognizes completed one-chapter scene JSON");
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    "History\n" + chapterPrompt + "\n" + """{"number":1,"title":"No scenes"}""",
    "History", chapterPrompt) is null,
    "Plain-main-text fallback rejects a chapter missing required scenes");
const string ideasPrompt = "Generate ideas. Required response_type zerovideo_ideas_v1.";
const string ideasJson = """{"response_type":"zerovideo_ideas_v1","ideas":[{"title":"A","hook":"B"}]}""";
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    "History\n" + ideasPrompt + "\n" + ideasJson,
    "History", ideasPrompt) == ideasJson,
    "Plain-main-text fallback validates fresh Ideas JSON");
const string publishPrompt = "Return response_type zerovideo_publish_v2.";
const string publishJson = """{"response_type":"zerovideo_publish_v2","youtube_title":"Title","youtube_description":"Details","tiktok_caption":"Caption"}""";
Check(ChatGptResponseRecovery.FindFreshJsonInMainText(
    "History\n" + publishPrompt + "\n" + publishJson,
    "History", publishPrompt) == publishJson,
    "Plain-main-text fallback validates fresh Publishing metadata JSON");

// Browser completion: a "Stop" control can remain visible after the
// response is done on some ChatGPT UI builds. Do not block on it forever.
Check(ChatGptResponseRecovery.CanAcceptStableJson(false, 2,
    TimeSpan.Zero, false),
    "Normal completed JSON is accepted without a Stop control");
Check(!ChatGptResponseRecovery.CanAcceptStableJson(true, 2,
    TimeSpan.FromSeconds(11), true),
    "Visible Stop control keeps a not-yet-stable reply pending");
Check(ChatGptResponseRecovery.CanAcceptStableJson(true, 2,
    TimeSpan.FromSeconds(12), true),
    "Valid complete reply recovers after 12 seconds despite stuck Stop control");
Check(!ChatGptResponseRecovery.CanAcceptStableJson(true, 2,
    TimeSpan.FromSeconds(30), false),
    "Stop-control fallback cannot accept absent or partial JSON");
Check(!ChatGptResponseRecovery.CanAcceptStableJson(true, 1,
    TimeSpan.FromSeconds(30), true),
    "Stop-control fallback requires repeated complete JSON observations");

// Distinct assistant JSON contract: a unique request ID and completion marker.
// These are tested against the page's mixed prompt/history/assistant text.
const string machineRequestId = "ZV_8e9b5a4f3c2d41ed9a326470c0f0a321";
const string differentRequestId = "ZV_00000000000000000000000000000000";
const string machineJson = """
{"zerovideo_request_id":"ZV_8e9b5a4f3c2d41ed9a326470c0f0a321","number":1,"title":"BTK","scenes":[{"number":1,"narration":"He was caught after the digital trace."}],"zerovideo_end_marker":"ZV_END_ZV_8e9b5a4f3c2d41ed9a326470c0f0a321"}
""";
const string olderMachineJson = """
{"zerovideo_request_id":"ZV_00000000000000000000000000000000","number":1,"scenes":[{"narration":"old"}],"zerovideo_end_marker":"ZV_END_ZV_00000000000000000000000000000000"}
""";
var machinePrompt = ChatGptResponseRecovery.BuildStrictJsonInstruction(machineRequestId);
Check(machinePrompt.Contains(machineRequestId, StringComparison.Ordinal) &&
      machinePrompt.Contains("zerovideo_end_marker", StringComparison.Ordinal) &&
      machinePrompt.Contains("valid JSON object", StringComparison.Ordinal),
    "Every ZeroVideo browser prompt requests a distinct parseable JSON document");
Check(ChatGptResponseRecovery.FindJsonForRequest(
    "Old answer\n" + olderMachineJson + "\nNew answer\n" + machineJson,
    machineRequestId) == machineJson,
    "Tagged JSON detector selects exact current request instead of older response");
Check(ChatGptResponseRecovery.FindJsonForRequest(
    "Old answer\n" + olderMachineJson + "\nNew answer\n" + machineJson,
    differentRequestId) == olderMachineJson,
    "Tagged JSON detector matches a different request only to that request's JSON");
Check(ChatGptResponseRecovery.FindJsonForRequest(
    machinePrompt + "\n" + new string('x', 61035) + "\n" + machineJson,
    machineRequestId) == machineJson,
    "Tagged JSON detector works in 61k-character transcripts with prompt text");
Check(ChatGptResponseRecovery.FindJsonForRequest(
    machinePrompt + "\n" + machineJson[..^3], machineRequestId) is null,
    "Truncated current JSON cannot be accepted before its final closing brace");
Check(ChatGptResponseRecovery.FindJsonForRequest(
    machinePrompt + "\n" + machineJson.Replace("zerovideo_end_marker", "invalid_end_key"),
    machineRequestId) is null,
    "Missing exact completion field is rejected even when JSON parses");
Check(ChatGptResponseRecovery.FindJsonForRequest(
    machinePrompt + "\n" + olderMachineJson,
    machineRequestId) is null,
    "The user's marked prompt and unrelated old JSON cannot satisfy current request");

// User-facing format is a literal Markdown JSON code fence with a Copy
// control, not bare JSON prose or an attachment. The reader validates the
// exact code block content even in a long conversation.
var jsonFence = new string((char)96, 3);
var fencedMachineJson = jsonFence + "json\n" + machineJson + "\n" + jsonFence;
Check(machinePrompt.Contains("Markdown fenced JSON CODE BLOCK", StringComparison.Ordinal) &&
      machinePrompt.Contains("visible Copy control", StringComparison.Ordinal) &&
      machinePrompt.Contains("OVERRIDES ANY EARLIER", StringComparison.Ordinal),
    "Strict JSON prompt requests a copyable fenced JSON code block, overriding no-markdown instructions");
Check(ChatGptResponseRecovery.FindJsonInCodeBlock(
    fencedMachineJson, machineRequestId) == machineJson,
    "Code-block parser extracts one valid fenced JSON document");
Check(ChatGptResponseRecovery.FindJsonInCodeBlock(
    machineJson, machineRequestId) == machineJson,
    "Code-block parser accepts rendered pre/code contents without literal fences");
Check(ChatGptResponseRecovery.FindJsonInCodeBlock(
    jsonFence + "json\n" + olderMachineJson + "\n" + jsonFence, machineRequestId) is null,
    "Code-block parser rejects an older request's JSON");
Check(ChatGptResponseRecovery.FindJsonInCodeBlock(
    fencedMachineJson[..^5], machineRequestId) is null,
    "Code-block parser rejects an incomplete or unclosed fenced response");
Check(ChatGptResponseRecovery.FindJsonInCodeBlock(
    jsonFence + "json\n" + machineJson.Replace("zerovideo_end_marker", "missing_marker") +
    "\n" + jsonFence, machineRequestId) is null,
    "Code-block parser requires the exact end marker");
Check(ChatGptResponseRecovery.HasEndMarker(machineJson, machineRequestId),
    "JSON completion marker can trigger targeted repair on parse failure");

var invalidNewlineTaggedJson = machineJson.Replace(
    "digital trace.", "digital\ntrace.", StringComparison.Ordinal);
Check(ChatGptResponseRecovery.FindJsonForRequest(
    invalidNewlineTaggedJson, machineRequestId) is null,
    "Malformed JSON with a real newline in narration is not accepted as valid");
Check(ChatGptResponseRecovery.FindTaggedCandidateForRepair(
    "Old replies\n" + invalidNewlineTaggedJson, machineRequestId)
    == invalidNewlineTaggedJson,
    "Malformed completed tagged JSON is extracted for correction rather than timing out");
Check(ChatGptResponseRecovery.FindTaggedCandidateForRepair(
    machinePrompt, machineRequestId) is null,
    "Prompt alone never triggers a fake JSON repair attempt");
Check(ChatGptResponseRecovery.FindTaggedCandidateForRepair(
    olderMachineJson, machineRequestId) is null,
    "Old tagged JSON is not treated as the current response");
Check(ChatGptResponseRecovery.FindTaggedCandidateForRepair(
    invalidNewlineTaggedJson[..^3], machineRequestId) is null,
    "Incomplete JSON without a final closing brace does not trigger premature correction");

// Regression: the first TikTok upload remains unchanged. The second upload
// must select the exact day with its year, not an identical day number in a
// neighboring month or a date from the previous scheduled video.
var tiktokSecondDate = new DateTime(2026, 11, 6, 18, 5, 0);
Check(TikTokSubsequentDatePolicy.MatchesDisplayedDate(
    "Nov 6, 2026", tiktokSecondDate),
    "Second-video date readback accepts the actual TikTok short month label");
Check(TikTokSubsequentDatePolicy.MatchesDisplayedDate(
    "November 6, 2026", tiktokSecondDate),
    "Second-video date readback accepts a full month label");
Check(TikTokSubsequentDatePolicy.MatchesDisplayedDate(
    "2026-11-06", tiktokSecondDate),
    "Second-video date readback accepts ISO date input value");
Check(!TikTokSubsequentDatePolicy.MatchesDisplayedDate(
    "Nov 5, 2026", tiktokSecondDate),
    "Second-video date readback rejects prior video's selected date");
Check(!TikTokSubsequentDatePolicy.MatchesDisplayedDate(
    "Nov 6, 2025", tiktokSecondDate),
    "Second-video date readback rejects an incorrect year");
Check(!TikTokSubsequentDatePolicy.MatchesDisplayedDate(
    "Nov 6", tiktokSecondDate),
    "Second-video date readback refuses a date without explicit year");
Check(TikTokSubsequentDatePolicy.MatchesCalendarDay(
    "Friday, November 6th, 2026", null, tiktokSecondDate),
    "Second-video recovery matches TikTok weekday and ordinal aria label");
Check(TikTokSubsequentDatePolicy.MatchesCalendarDay(
    null, "2026-11-06", tiktokSecondDate),
    "Second-video recovery matches exact ISO date attribute");
Check(!TikTokSubsequentDatePolicy.MatchesCalendarDay(
    "Saturday, December 6th, 2026", null, tiktokSecondDate),
    "Second-video recovery rejects same day in a different month");
Check(!TikTokSubsequentDatePolicy.MatchesCalendarDay(
    "Friday, November 6th, 2025", null, tiktokSecondDate),
    "Second-video recovery rejects identical day in wrong year");
Check(TikTokSubsequentDatePolicy.MonthsToTarget(
    2026, 10, tiktokSecondDate) == 1,
    "Second-video picker navigates correctly from October to November");
Check(TikTokSubsequentDatePolicy.MonthsToTarget(
    2026, 12, tiktokSecondDate) == -1,
    "Second-video picker navigates correctly backward to requested month");
Check(TikTokSubsequentDatePolicy.MonthsToTarget(
    2025, 12, new DateTime(2026, 1, 1)) == 1,
    "Second-video picker handles calendar year transitions safely");

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

    // A single mapping now drives video cut positions, ASS and SRT captions.
    var spokenChapter = new ProjectChapter
    {
        Number = 2,
        Narration = "Zak Bagans entered an abandoned corridor and opened " +
            "a locked door. He discovered an old cassette tape on a desk " +
            "beside a dusty folder.",
        Scenes =
        [
            new ProjectScene
            {
                Number = 1, TargetSeconds = 8,
                Narration = "Zak Bagans entered an abandoned corridor " +
                    "and opened a locked door.",
                ImagePrompt = "Wide shot of Zak Bagans walking through " +
                    "the corridor toward a wooden door.",
                VisualQuery = "abandoned corridor wooden door"
            },
            new ProjectScene
            {
                Number = 2, TargetSeconds = 8,
                Narration = "He discovered an old cassette tape on a desk " +
                    "beside a dusty folder.",
                ImagePrompt = "Object close-up: cassette tape beside a " +
                    "dusty investigation folder on a wooden desk.",
                VisualQuery = "cassette tape vintage desk"
            }
        ]
    };
    Check(NarrationTimelineService.ScriptsMatch(spokenChapter),
        "Chapter voiceover matches its concatenated scene narration");
    var speechIntervals = new List<(double Start,double End)>
    {
        (0.3,6.1),(6.6,12.7),(13.2,18.8)
    };
    var timing = NarrationTimelineService.Plan(
        spokenChapter,20.0,speechIntervals);
    Check(timing.Scenes.Count==2 &&
        Math.Abs(timing.Scenes[0].Start)<0.001 &&
        Math.Abs(timing.Scenes[^1].End-20)<0.001 &&
        Math.Abs(timing.Scenes.Sum(s=>s.Duration)-20)<0.001,
        "Scene timing partitions the exact real TTS duration without drift");
    Check(timing.Scenes[0].End>3 &&
        timing.Scenes[0].End<17 &&
        timing.Scenes[1].Start==timing.Scenes[0].End,
        "Scene cuts follow narration proportions and detected pauses");
    var cues = NarrationTimelineService.CaptionCues(
        spokenChapter,timing);
    Check(cues.Count>=4 &&
        cues.Zip(cues.Skip(1)).All(pair=>
            pair.First.Time.End<=pair.Second.Time.Start+0.00001),
        "Burned caption phrases never overlap or mix subsequent scenes");
    Check(cues.All(c=>c.Time.End<=20.001 &&
        c.Time.Start>=0 && c.Time.Duration>0),
        "Caption timings stay inside narrated chapter duration");
    Check(cues.All(c=>CaptionLayoutService.FitsInsideSafeWidth(c.Words)),
        "Every timed caption fits within the 9:16 safe area");
    Check(CaptionLayoutService.FitsInsideSafeWidth(
        ["EXTRAORDINARILYLONGWORD","INVESTIGATORS","DISCOVERED"]),
        "Long words fit without clipping and with no third ASS line");
    Check(CaptionLayoutService.Format(
        ["THE","HIDDEN","CASSETTE","TAPE"])
        .Contains("\\q2"),
        "ASS captions prevent implicit wrapping by libass");

    var savedDraft = new ProjectChapter
    {
        Narration = "An authentic recording was found at the abandoned house.",
        Scenes =
        [
            new ProjectScene { Narration="A totally different script." },
            new ProjectScene { Narration="Another original draft." }
        ]
    };
    Check(!NarrationTimelineService.ScriptsMatch(savedDraft),
        "Legacy chapter narration mismatch is detected");
    var voiceWords=NarrationTimelineService.SpokenSceneWords(savedDraft);
    Check(string.Join(" ",voiceWords.SelectMany(w=>w))
        .Contains("authentic recording",StringComparison.OrdinalIgnoreCase) &&
        !string.Join(" ",voiceWords.SelectMany(w=>w))
            .Contains("different script",StringComparison.OrdinalIgnoreCase),
        "Captions follow the actual TTS script instead of mixing old scene drafts");

    var wide = SceneVisualPlanService.Compose(
        spokenChapter.Scenes[0].Narration,
        spokenChapter.Scenes[0].ImagePrompt,
        spokenChapter.Scenes[0].VisualQuery,0,true);
    var objectShot = SceneVisualPlanService.Compose(
        spokenChapter.Scenes[1].Narration,
        spokenChapter.Scenes[1].ImagePrompt,
        spokenChapter.Scenes[1].VisualQuery,1,true);
    Check(wide.Name=="wide" && wide.Wide &&
        wide.IdentityMultiplier<1.0 &&
        wide.Instruction.Contains("full body",StringComparison.OrdinalIgnoreCase),
        "Real-person identity on wide shots keeps full-body action and environment");
    Check(objectShot.Name=="detail" && !objectShot.Wide &&
        !objectShot.Instruction.Contains("facial close-up",
            StringComparison.OrdinalIgnoreCase),
        "Evidence/object narration uses an object detail, never a face headshot");
    var explicitPortrait=SceneVisualPlanService.Compose(
        "The investigator's expression turned fearful.",
        "A close-up face portrait of the investigator.",
        "expression portrait",5,true);
    Check(explicitPortrait.Name=="face",
        "An explicitly scripted face portrait remains eligible for close-up");
    var differentA=SceneVisualPlanService.Compose(
        "The witness testified.","Investigators investigate",
        "courtroom",0,true);
    var differentB=SceneVisualPlanService.Compose(
        "The witness testified.","Investigators investigate",
        "courtroom",1,true);
    Check(differentA.Name!=differentB.Name,
        "Different independent scene seeds vary unspecified camera distance");
    Check(SceneVisualPlanService.Relevance(
        spokenChapter.Scenes[0],spokenChapter.Scenes[1])<
        SceneVisualPlanService.Relevance(
            spokenChapter.Scenes[0],spokenChapter.Scenes[0]),
        "Missing media selection prefers genuinely matching narrated evidence");
    Check(SceneVisualPlanService.Relevance(
        new ProjectScene{ImagePrompt="The Zodiac cipher written on paper"},
        new ProjectScene{ImagePrompt="An empty ocean with sailing ship"})<0.2,
        "Unrelated generic footage cannot replace a missing script-specific scene");

    // Exact 16:9 / 9:16 generation resolution presets, persisted default.
    var settings = new ZeroVideoSettings();
    Check(settings.LocalImageResolutionPreset == "Fast" &&
        settings.LocalImageResolutionMode == "Direct",
        "Existing 8 GB GPU workflows keep the original Fast direct default");
    var presets = new[]
    {
        ("Fast",1024,576),
        ("Balanced",1280,720),
        ("High",1600,900),
        ("Ultra",1920,1080)
    };
    foreach(var (preset,width,height) in presets)
    {
        var horizontal=LocalImageResolutionService.Resolve(
            false,preset,"Direct");
        var vertical=LocalImageResolutionService.Resolve(
            true,preset,"Direct");
        Check(horizontal.Output.Width==width &&
              horizontal.Output.Height==height &&
              horizontal.Working.Width==width &&
              horizontal.Working.Height==height,
            preset+" Direct uses the requested landscape latent resolution");
        Check(vertical.Output.Width==height &&
              vertical.Output.Height==width &&
              vertical.Working.Width==height &&
              vertical.Working.Height==width,
            preset+" Direct uses correct 9:16 portrait pixel dimensions");
        var safe=LocalImageResolutionService.Resolve(
            false,preset,"Upscale");
        Check(safe.Output.Width==width && safe.Output.Height==height &&
              safe.Working.Width==1024 && safe.Working.Height==576,
            preset+" CPU Upscale retains final landscape output dimensions");
        var verticalSafe=LocalImageResolutionService.Resolve(
            true,preset,"Upscale");
        Check(verticalSafe.Working.Width==576 &&
              verticalSafe.Working.Height==1024 &&
              verticalSafe.Output.Width==height &&
              verticalSafe.Output.Height==width,
            preset+" CPU Upscale retains final vertical output dimensions");
    }
    Check(LocalImageResolutionService.Resolve(false,"Ultra","Upscale")
        .CpuUpscale && !LocalImageResolutionService.Resolve(false,
        "Ultra","Direct").CpuUpscale,
        "Ultra CPU Upscale avoids allocating a native 1920x1080 GPU latent");
    Check(LocalImageResolutionService.Resolve(false,"Ultra","Direct")
        .UsesHighGpuMemory && !LocalImageResolutionService.Resolve(false,
        "Ultra","Upscale").UsesHighGpuMemory,
        "User receives a high-VRAM warning only for expensive direct generation");
    Check(LocalImageResolutionService.ResolveOutput(false,"Bogus").Width==1024 &&
          !LocalImageResolutionService.IsValidPreset("Bogus") &&
          !LocalImageResolutionService.IsValidMode("Bogus"),
        "Unknown saved options safely return to Fast/Direct");

    // Regression: before this fix, .SRT exported but 16:9 MP4
    // received NO visible subtitles because burn-in required 9:16.
    var defaultCaptionOptions = new ProjectOptions();
    Check(defaultCaptionOptions.GenerateCaptions &&
        defaultCaptionOptions.BurnInBoldCaptions,
        "New Longform projects default to SRT and visible burned captions");
    Check(CaptionRenderPolicy.ShouldBurn(defaultCaptionOptions,"16:9"),
        "16:9 Longform Automation now burns visible captions into MP4");
    Check(CaptionRenderPolicy.ShouldBurn(defaultCaptionOptions,"9:16"),
        "9:16 Shorts keeps existing visible caption burn-in");
    Check(!CaptionRenderPolicy.ShouldBurn(defaultCaptionOptions,"1:1"),
        "Unsupported aspect ratios do not silently apply 16:9 captions");
    defaultCaptionOptions.GenerateCaptions=false;
    Check(CaptionRenderPolicy.ShouldBurn(defaultCaptionOptions,"16:9"),
        "Burned Longform captions work without an external .SRT sidecar");
    defaultCaptionOptions.BurnInBoldCaptions=false;
    Check(!CaptionRenderPolicy.ShouldBurn(defaultCaptionOptions,"16:9"),
        "Longform Burn Captions toggle opt-out is respected");
    Check(CaptionRenderPolicy.AssCanvas("16:9").Contains("PlayResX: 1920") &&
        CaptionRenderPolicy.AssCanvas("16:9").Contains("PlayResY: 1080"),
        "Longform captions render on correct 1920x1080 ASS canvas");
    Check(CaptionRenderPolicy.AssCanvas("9:16").Contains("PlayResX: 1080") &&
        CaptionRenderPolicy.AssCanvas("9:16").Contains("PlayResY: 1920"),
        "Shorts retain their original 1080x1920 ASS canvas");
    Check(CaptionRenderPolicy.AssStyle("16:9").Contains(",195,195,118,1") &&
        CaptionRenderPolicy.AssStyle("9:16").Contains(",145,145,350,1"),
        "Landscape and portrait margins are distinct and safe");
    var widescreenCaptions = new List<string[]>
    {
        new[] {"THE","CASE","BEGAN"},
        new[] {"JEFFREY","DAHMER","INVESTIGATORS"},
        new[] {"EXTRAORDINARILYLONGWORD","LONGEST","INTERVIEW"},
        new[] {"EVIDENCE","POINTED","SOMEWHERE"},
        new[] {"ZAK","BAGANS","DISAPPEARED"}
    };
    foreach(var words in widescreenCaptions)
    {
        Check(CaptionLayoutService.FitsInsideSafeWidth(words,true),
            "16:9 caption remains on-screen and at most two lines: "+
            string.Join(" ",words));
        var formatted=CaptionLayoutService.Format(words,true);
        Check(formatted.Contains("\\q2") &&
            formatted.Split("\\N").Length<=2,
            "16:9 caption explicitly disables libass automatic wrapping");
    }
    Check(CaptionLayoutService.FitsInsideSafeWidth(
        ["THE","FINAL","RECORDING"],false),
        "16:9 caption change does not modify Shorts 9:16 safe margins");

    var thumbnailTitles = new[]
    {
        ("John Wayne Gacy — The Killer Who Buried Victims Beneath His House", "BURIED VICTIMS"),
        ("Jeffrey Dahmer — The Serial Killer Who Escaped Police Suspicion", "ESCAPED POLICE SUSPICION"),
        ("Ted Bundy — The Charming Killer Who Escaped Twice", "ESCAPED TWICE"),
        ("Zak Bagans — The Ghost Hunter Who Built an Empire Around the Paranormal", "BUILT"),
        ("The Black Dahlia Murder — Hollywood's Most Haunting Unsolved Mystery", "UNSOLVED MYSTERY")
    };
    foreach (var (title, expected) in thumbnailTitles)
    {
        var hook=ThumbnailHeadlineService.Hook(title,"",0);
        Check(hook.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=5,
            "Thumbnail hook limited to five words: "+title);
        Check(hook.Contains(expected,StringComparison.OrdinalIgnoreCase),
            "Thumbnail text accurately reflects source title: "+hook);
        Check(ThumbnailHeadlineService.Lines(hook).Count<=2,
            "Large thumbnail headline has at most two lines");
        Check(ThumbnailHeadlineService.Hook(title,"",1).Split(' ').Length<=5,
            "Alternate regenerated hook remains concise");
    }
    Check(!ThumbnailHeadlineService.Hook("The Zodiac Killer","",0)
        .Contains("CAUGHT",StringComparison.Ordinal),
        "Unsolved case is never given fabricated caught claim");
    Console.WriteLine("Character Bible regression checks passed.");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
