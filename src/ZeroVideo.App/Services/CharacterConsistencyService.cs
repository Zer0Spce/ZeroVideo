using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Conservative per-project visual character reference handling. Detects only
/// recurring named entities / clearly human roles in at least two different
/// scene beats. Users can edit or override its automatic suggestions.
/// </summary>
public static class CharacterConsistencyService
{
    private static readonly Regex PersonNames=new(
        @"(?<![\p{L}])[A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,}){1,2}(?![\p{L}])",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly string[] Roles=
    [
        "detective","investigator","journalist","reporter","scientist",
        "police officer","lawyer","doctor","historian","narrator","host"
    ];
    private static readonly HashSet<string> Rejected=new(StringComparer.OrdinalIgnoreCase)
    {
        "Comic Book","Cinematic Comic","Editorial Cartoon","Dark Night",
        "New York","Los Angeles","Chapter One","Chapter Two","Scene One",
        "The Detective","The Investigator","The Police","The Man","The Woman",
        "The Story","United States","Black And","Long Form","True Crime",
        "Vintage Comic","Modern Comic"
    };

    public static IReadOnlyList<ProjectCharacter> EnsureProfiles(VideoProject project)
    {
        project.Characters ??= [];
        if(!project.Options.EnableCharacterConsistency)return project.Characters;
        var scenes=project.Chapters.SelectMany(c=>c.Scenes).ToList();
        var appearances=new Dictionary<string,(int Count,string Context)>(
            StringComparer.OrdinalIgnoreCase);
        foreach(var scene in scenes)
        {
            var text=string.Join(" ",new[]{scene.Narration,scene.ImagePrompt,scene.VisualQuery}
                .Where(x=>!string.IsNullOrWhiteSpace(x)));
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(Match hit in PersonNames.Matches(text))
            {
                var candidate=hit.Value.Trim();
                if(candidate.Length>45||Rejected.Contains(candidate) ||
                   candidate.StartsWith("The ",StringComparison.Ordinal) ||
                   candidate.StartsWith("One ",StringComparison.Ordinal))
                    continue;
                names.Add(candidate);
            }
            // Generic character roles are included only if explicitly depicted
            // in multiple scene prompts. This avoids treating every occurrence
            // of the word "man" as one particular individual.
            foreach(var role in Roles)
            {
                if(Regex.IsMatch(scene.ImagePrompt??"",
                    @"(?<!\w)"+Regex.Escape(role)+@"(?!\w)",
                    RegexOptions.IgnoreCase))
                    names.Add(char.ToUpperInvariant(role[0])+role[1..]);
            }
            foreach(var name in names)
            {
                var context=scene.ImagePrompt??"";
                if(appearances.TryGetValue(name,out var v))
                    appearances[name]=(v.Count+1,v.Context);
                else appearances[name]=(1,context);
            }
        }
        foreach(var entry in appearances.Where(x=>x.Value.Count>=2)
            .OrderByDescending(x=>x.Value.Count).Take(6))
        {
            if(project.Characters.Any(c=>c.Name.Equals(entry.Key,
                StringComparison.OrdinalIgnoreCase)))continue;
            project.Characters.Add(new ProjectCharacter
            {
                Name=entry.Key,
                Appearance="Keep the same recognizable face, hair, apparent age, "+
                    "wardrobe, accessories and distinctive features in every scene. "+
                    entry.Value.Context[..Math.Min(220,entry.Value.Context.Length)],
                Seed=StableSeed(project.Id+"|"+entry.Key)
            });
        }
        foreach(var character in project.Characters)
            if(character.Seed<=0)
                character.Seed=StableSeed(project.Id+"|"+character.Name);
        return project.Characters;
    }

    public static IReadOnlyList<ProjectCharacter> Match(VideoProject project,ProjectScene scene)
    {
        if(!project.Options.EnableCharacterConsistency)return [];
        var text=(scene.Narration??"")+" "+(scene.ImagePrompt??"")+" "+
            (scene.VisualQuery??"");
        return (project.Characters??[]).Where(p=>p.Enabled &&
            !string.IsNullOrWhiteSpace(p.Name) &&
            Regex.IsMatch(text,@"(?<!\w)"+Regex.Escape(p.Name.Trim())+@"(?!\w)",
                RegexOptions.IgnoreCase)).ToList();
    }

    public static long StableSeed(string input)
    {
        var hash=SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return (long)(BitConverter.ToUInt64(hash,0) & 0x7fffffffffffffffUL)+1;
    }

    public static string AnchorDirectory(VideoProject project)=>
        Path.Combine(project.ProjectDirectory,"CharacterReferences");

    public static string CaptureFirstReference(VideoProject project,
        ProjectCharacter profile,string generatedPng)
    {
        if(!File.Exists(generatedPng))throw new FileNotFoundException("Scene image missing",generatedPng);
        if(string.IsNullOrWhiteSpace(project.ProjectDirectory))
            throw new InvalidOperationException("Project must be saved before character references.");
        var folder=AnchorDirectory(project);
        Directory.CreateDirectory(folder);
        var safe=Regex.Replace(profile.Name.Trim(),@"[^A-Za-z0-9_-]","_");
        if(safe.Length==0)safe="Character";
        var target=Path.Combine(folder,safe+"-"+profile.Seed+".png");
        if(!File.Exists(target))File.Copy(generatedPng,target);
        profile.ReferenceImagePath=target;
        return target;
    }

    public static string? GetExistingReference(VideoProject project,ProjectCharacter character)
    {
        var path=character.ReferenceImagePath;
        if(!string.IsNullOrWhiteSpace(path)&&File.Exists(path))return path;
        // Existing visuals can seed a master anchor on resumed projects:
        // only reuse a previous scene that actually names the same character.
        foreach(var chapter in project.Chapters)
        foreach(var scene in chapter.Scenes)
        {
            if(!Match(project,scene).Any(p=>ReferenceEquals(p,character)))continue;
            if(!string.IsNullOrWhiteSpace(scene.MediaPath)&&
               File.Exists(scene.MediaPath))return scene.MediaPath;
        }
        return null;
    }
}
