using System.Text.Json.Serialization;

namespace ZeroVideo.App.Models;

public sealed class VideoProject
{
    public string Id { get; set; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    public string Title { get; set; } = "Untitled ZeroVideo Project";
    public string Topic { get; set; } = "";
    public int TargetMinutes { get; set; } = 10;
    public string AspectRatio { get; set; } = "16:9";
    public string Style { get; set; } = "Documentary explainer";
    public string Language { get; set; } = "English";
    public string Voice { get; set; } = "";
    public string? ChatGptConversationUrl { get; set; }
    public int? VisualAssetVersion { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<ProjectChapter> Chapters { get; set; } = [];
    /// <summary>
    /// Optional local-image character identities. No cloud service receives
    /// anchor images. References are copied into the project on approval.
    /// </summary>
    public List<ProjectCharacter> Characters { get; set; } = [];
    /// <summary>
    /// Opt-in, user-provided references for real, named people. References
    /// are never collected from the internet or added automatically.
    /// Separate from the legacy fictional Character Bible.
    /// </summary>
    public List<RealPersonIdentity> RealPeople { get; set; } = [];
    // Per-video auto identity lifecycle, persisted for interrupted-job resume.
    // Never points to another project's temporary identity folder.
    public string ActiveAutoIdentityPerson { get; set; } = "";
    public bool AutoIdentityEnabledByVideo { get; set; } = false;


    public List<string> Sources { get; set; } = [];
    public ProjectOptions Options { get; set; } = new();
    [JsonIgnore] public string ProjectDirectory { get; set; } = "";
}

public sealed class ProjectOptions
{
    public bool GenerateCaptions { get; set; } = true;
    // Burn large outlined uppercase captions into vertical MP4s. Separate from SRT export.
    public bool BurnInBoldCaptions { get; set; } = true;
    public bool AddMotion { get; set; } = true;
    public bool ContinuousMotion { get; set; } = true;
    public double MotionCycleSeconds { get; set; } = 5.0;
    public string MotionStyle { get; set; } = "Automatic";
    // A lightweight, GPU-free, periodic image motion for every generated
    // still. It does not pretend to synthesize AI facial/limb animation.
    // Short cycles are repeated to match each full narration scene.
    public bool SubtleImageAnimation { get; set; } = true;
    public double SubtleImageAnimationSeconds { get; set; } = 4.0;
    public bool SceneTransitions { get; set; } = true;
    public string TransitionStyle { get; set; } = "Automatic";
    public double TransitionDurationSeconds { get; set; } = 0.35;
    public bool CinematicEffects { get; set; } = true;
    public string EffectIntensity { get; set; } = "Subtle";
    public bool StyleAwareEffects { get; set; } = true;
    public bool SmartParallax { get; set; } = true;
    public bool AvoidRepeatedEffects { get; set; } = true;
    public bool AddBackgroundMusic { get; set; }
    public bool AddSoundEffects { get; set; }
    public bool AvoidRepeatedVisuals { get; set; } = true;
    public bool GenerateMissingVisuals { get; set; } = true;
    // Opt in only for projects with visibly recurring people. Reusing an
    // entire scene as a reference can otherwise lock its background/layout.
    public bool EnableCharacterConsistency { get; set; } = false;
    // Identity conditioning only applies to visible, explicitly named people.
    // Keep default OFF: local FLUX Schnell + PuLID is GPU-intensive and
    // requires an independently installed compatible ComfyUI identity adapter.
    public bool EnableRealPersonIdentityLock { get; set; } = false;
    public double RealPersonIdentityStrength { get; set; } = 0.75;
    // Default ON for new projects: automatically handle clear real-person
    // titles when Local FLUX plus its PuLID adapter are ready. Never
    // interrupt normal video creation for an unavailable identity adapter.
    public bool AutoRealPersonIdentity { get; set; } = true;
    public int AutoIdentityPortraitCount { get; set; } = 2;

    // Local ComfyUI only: render three distinct vintage panels and assemble a comic page.
    public bool VintageComicPanelComposition { get; set; } = true;


    /// <summary>
    /// Exact chapter count requested from ChatGPT for the master outline.
    /// </summary>
    public int HowManyChapters { get; set; } = 7;

    /// <summary>
    /// Exact number of narration/visual scenes requested inside every chapter.
    /// One generated image is targeted for each scene, so this is also the normal
    /// image count per chapter when the safety total image cap does not intervene.
    /// </summary>
    public int ImagesPerChapter { get; set; } = 10;

    /// <summary>
    /// Hard project-wide safety cap for actual ChatGPT-generated images.
    /// It can intentionally stop image generation before every scripted scene has
    /// its own image; rendering then reuses the nearest real visual instead of black.
    /// </summary>
    public int MaxGeneratedVisuals { get; set; } = 120;

    // Compatibility aliases for projects written by the immediately previous builds.
    // New UI/pipeline code uses HowManyChapters and ImagesPerChapter.
    public int MaxGeneratedScenes { get => HowManyChapters; set => HowManyChapters = value; }
    public int ImagesPerScene { get => ImagesPerChapter; set => ImagesPerChapter = value; }

    public string? BackgroundMusicPath { get; set; }
    public double BackgroundMusicVolume { get; set; } = 0.10;
}

public sealed class ProjectChapter
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Narration { get; set; } = "";
    public int TargetSeconds { get; set; }
    public string? AudioPath { get; set; }
    public List<ProjectScene> Scenes { get; set; } = [];
}

public sealed class ProjectScene
{
    public int Number { get; set; }
    public string Role { get; set; } = "detail";
    public string Narration { get; set; } = "";
    public string VisualType { get; set; } = "broll";
    public string VisualQuery { get; set; } = "";
    public string ImagePrompt { get; set; } = "";
    public string ShotDirection { get; set; } = "slow push in";
    public double TargetSeconds { get; set; } = 6;
    public string? MediaPath { get; set; }
    public List<string> MediaPaths { get; set; } = [];
    public string? SoundEffectPath { get; set; }
}

public sealed class ProjectCharacter
{
    public string Name { get; set; } = "";
    public string Appearance { get; set; } = "";
    public string ReferenceImagePath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long Seed { get; set; } = 0;
}

public sealed class RealPersonIdentity
{
    public string Name { get; set; } = "";
    // Up to three explicit user-approved local portrait references.
    public List<string> ReferencePaths { get; set; } = [];
    // Synthetic guides are never treated as verified photo likenesses.
    public List<string> AiApproximateReferencePaths { get; set; } = [];
    // These references were created automatically for THIS project's
    // current video only. Cleanup is limited to these exact paths.
    public List<string> AutoTemporaryReferencePaths { get; set; } = [];
    public bool AutoCreatedForVideo { get; set; } = false;
    public bool Enabled { get; set; } = true;
}
