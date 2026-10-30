using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

// All account data is imported on the user's machine or read from a page
// they explicitly open. No TikTok credentials, cookies or private API
// endpoints are requested or saved.
public sealed class TikTokPerformanceRecord
{
    public string Title { get; set; } = "";
    public DateTime PublishedLocal { get; set; }
    public long Views { get; set; }
    public long Likes { get; set; }
    public long Comments { get; set; }
    public long Shares { get; set; }
}

public sealed class TikTokIntelligenceData
{
    public DateTimeOffset? StudioCapturedAt { get; set; }
    public string StudioVisibleAnalytics { get; set; } = "";
    public DateTimeOffset? CreativeCenterCapturedAt { get; set; }
    public string TrendRegion { get; set; } = "Global (as displayed by TikTok)";
    public List<string> CreativeCenterHashtags { get; set; } = [];
    public DateTimeOffset? CsvImportedAt { get; set; }
    public List<TikTokPerformanceRecord> Videos { get; set; } = [];
}

public sealed partial class MainViewModel
{
    private static readonly string IntelligenceFile = Path.Combine(
        AppPaths.DataDirectory,"tiktok-intelligence.json");
    private TikTokIntelligenceData _tiktokIntelligence = new();
    private string _tikTokAnalyticsStatus = "Not connected. Open TikTok Studio, then capture its visible Analytics page or import your analytics CSV.";
    private string _tikTokTrendsStatus = "No TikTok Creative Center trends captured.";
    private string _tikTokInsightsSummary = "No measured account analytics yet. AI recommendations use general heuristics.";
    private bool _intelligenceBusy;

    public string TikTokAnalyticsStatus
    {
        get => _tikTokAnalyticsStatus; private set => Set(ref _tikTokAnalyticsStatus,value);
    }
    public string TikTokTrendsStatus
    {
        get => _tikTokTrendsStatus; private set => Set(ref _tikTokTrendsStatus,value);
    }
    public string TikTokInsightsSummary
    {
        get => _tikTokInsightsSummary; private set => Set(ref _tikTokInsightsSummary,value);
    }
    public bool IntelligenceBusy
    {
        get => _intelligenceBusy; private set => Set(ref _intelligenceBusy,value);
    }

    public ICommand OpenTikTokAnalyticsCommand { get; private set; } = null!;
    public ICommand CaptureTikTokAnalyticsCommand { get; private set; } = null!;
    public ICommand ImportTikTokAnalyticsCsvCommand { get; private set; } = null!;
    public ICommand OpenTikTokCreativeCenterCommand { get; private set; } = null!;
    public ICommand CaptureTikTokTrendsCommand { get; private set; } = null!;

    private void InitializeTikTokIntelligenceCommands()
    {
        OpenTikTokAnalyticsCommand = new AsyncCommand(async () =>
        {
            try
            {
                // A regular Edge window for manual TikTok authentication,
                // not automated sign-in, password scraping or private APIs.
                await _browser.OpenManualAsync(Settings,
                    "https://www.tiktok.com/tiktokstudio/analytics");
                TikTokAnalyticsStatus = "TikTok Studio opened. Sign in normally and navigate to Analytics. " +
                    "After it loads, return here and click Capture Visible Analytics.";
            }
            catch(Exception ex) { TikTokAnalyticsStatus = "Could not open TikTok Studio: "+ex.Message; }
        });
        CaptureTikTokAnalyticsCommand = new AsyncCommand(CaptureTikTokAnalyticsAsync);
        ImportTikTokAnalyticsCsvCommand = new AsyncCommand(ImportTikTokAnalyticsCsvAsync);
        OpenTikTokCreativeCenterCommand = new AsyncCommand(async () =>
        {
            try
            {
                await _browser.OpenManualAsync(Settings,
                    "https://ads.tiktok.com/business/creativecenter/inspiration/popular/hashtag/pc/en");
                TikTokTrendsStatus = "TikTok Creative Center opened. Choose the region, category and " +
                    "time range, then return here and click Capture Visible Trends.";
            }
            catch(Exception ex) { TikTokTrendsStatus = "Could not open Creative Center: "+ex.Message; }
        });
        CaptureTikTokTrendsCommand = new AsyncCommand(CaptureTikTokTrendsAsync);
    }

    private void LoadTikTokIntelligence()
    {
        try
        {
            if(File.Exists(IntelligenceFile))
                _tiktokIntelligence = JsonSerializer.Deserialize<TikTokIntelligenceData>(
                    File.ReadAllText(IntelligenceFile)) ?? new();
            RefreshTikTokIntelligenceSummary();
        }
        catch(Exception ex)
        {
            TikTokAnalyticsStatus = "Saved analytics could not be opened: "+ex.Message;
            _tiktokIntelligence = new();
        }
    }
    private void SaveTikTokIntelligence()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temp = IntelligenceFile+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(_tiktokIntelligence,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp,IntelligenceFile,true);
        RefreshTikTokIntelligenceSummary();
    }

    // Read-only. No clicks inside the Studio page, and no cookie, token,
    // request-header or local-storage extraction.
    private async Task CaptureTikTokAnalyticsAsync()
    {
        if(IntelligenceBusy) return;
        IntelligenceBusy = true;
        try
        {
            await _browser.EnsureStartedAsync(Settings);
            var pages=_browser.Context.Pages.Where(p =>
                Uri.TryCreate(p.Url,UriKind.Absolute,out var u) &&
                u.Host.EndsWith("tiktok.com",StringComparison.OrdinalIgnoreCase) &&
                u.AbsolutePath.Contains("tiktokstudio",StringComparison.OrdinalIgnoreCase) &&
                u.AbsolutePath.Contains("analytic",StringComparison.OrdinalIgnoreCase)).ToList();
            if(pages.Count==0)
            {
                TikTokAnalyticsStatus = "No TikTok Studio Analytics page found in ZeroVideo's dedicated Edge profile. " +
                    "Open Studio Analytics first, log in, and select the Analytics screen.";
                return;
            }
            var text=await pages[^1].EvaluateAsync<string>(@"
()=> {
 const main=document.querySelector('main')||document.body;
 const visible=e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);
   return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden';};
 if(!visible(main))return '';
 return (main.innerText||'').slice(0,30000);
}");
            if(string.IsNullOrWhiteSpace(text) || text.Length<70 ||
                !(text.Contains("view",StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("follower",StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("engagement",StringComparison.OrdinalIgnoreCase)))
            {
                TikTokAnalyticsStatus = "This Studio page does not show readable analytics metrics. " +
                    "Switch to Overview/Content and retry, or import an analytics CSV.";
                return;
            }
            _tiktokIntelligence.StudioVisibleAnalytics=text;
            _tiktokIntelligence.StudioCapturedAt=DateTimeOffset.Now;
            SaveTikTokIntelligence();
            TikTokAnalyticsStatus = $"Read-only Studio analytics snapshot saved locally at " +
                $"{DateTime.Now:yyyy-MM-dd HH:mm}. This is visible page text, not full " +
                "per-video history; import CSV for evidence-based posting-hour rankings.";
        }
        catch(Exception ex)
        {
            TikTokAnalyticsStatus = "TikTok Studio analytics capture failed: "+ex.Message +
                ". You can still import the exported analytics CSV.";
        }
        finally { IntelligenceBusy = false; }
    }

    private static long ParseMetric(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return 0;
        var value=raw.Trim().Replace(",","").Replace(" ","");
        var m=Regex.Match(value,@"^([\d.]+)([KMB])?$",RegexOptions.IgnoreCase);
        if(m.Success &&
            double.TryParse(m.Groups[1].Value,NumberStyles.Float,
                CultureInfo.InvariantCulture,out var number))
        {
            var multiplier=m.Groups[2].Value.ToUpperInvariant() switch
            { "K"=>1000.0,"M"=>1000000.0,"B"=>1000000000.0,_=>1.0 };
            return (long)Math.Clamp(Math.Round(number*multiplier),0,long.MaxValue);
        }
        return long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,
            out var exact) ? Math.Max(0,exact) : 0;
    }

    private static int FindCsvColumn(string[] headers,params string[] matches)
    {
        for(var i=0;i<headers.Length;i++)
        {
            var key=Regex.Replace(headers[i].ToLowerInvariant(),@"[^a-z0-9]","");
            if(matches.Any(pattern=>key.Contains(pattern)))return i;
        }
        return -1;
    }

    private async Task ImportTikTokAnalyticsCsvAsync()
    {
        if(IntelligenceBusy)return;
        var picker=new OpenFileDialog
        {
            Title="Import TikTok analytics export",
            Filter="CSV analytics|*.csv;*.tsv|All files|*.*",
            CheckFileExists=true
        };
        if(picker.ShowDialog()!=true)return;
        IntelligenceBusy=true;
        try
        {
            var records=await Task.Run(()=>
            {
                using var parser=new TextFieldParser(picker.FileName,Encoding.UTF8);
                var firstLine=File.ReadLines(picker.FileName).FirstOrDefault() ?? "";
                parser.TextFieldType=FieldType.Delimited;
                parser.SetDelimiters(firstLine.Count(c=>c=='\t')>firstLine.Count(c=>c==',')
                    ? "\t" : ",");
                parser.HasFieldsEnclosedInQuotes=true;
                var headers=parser.ReadFields() ??
                    throw new InvalidDataException("CSV has no column headers.");
                var time=FindCsvColumn(headers,"postdate","publishdate",
                    "publisheddate","posttime","createdtime","createdat","dateposted","datetime","date");
                var views=FindCsvColumn(headers,"videoviews","totalviews","viewcount","views");
                var likes=FindCsvColumn(headers,"likes","likecount");
                var comments=FindCsvColumn(headers,"comments","commentcount");
                var shares=FindCsvColumn(headers,"shares","sharecount");
                var title=FindCsvColumn(headers,"videotitle","title","description");
                if(time<0||views<0)
                    throw new InvalidDataException("CSV must contain a posting date/time and view count. " +
                        "TikTok exports vary; open your per-video analytics CSV, not the follower totals export.");
                var data=new List<TikTokPerformanceRecord>();
                while(!parser.EndOfData&&data.Count<3000)
                {
                    var fields=parser.ReadFields();
                    if(fields==null||fields.Length<=Math.Max(time,views))continue;
                    if(!DateTime.TryParse(fields[time],CultureInfo.CurrentCulture,
                        DateTimeStyles.AssumeLocal,out var timestamp) &&
                       !DateTime.TryParse(fields[time],CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,out timestamp))continue;
                    if(timestamp.Year<2020 || timestamp>DateTime.Now.AddDays(2))continue;
                    string Field(int i)=>i>=0&&i<fields.Length ? fields[i] : "";
                    data.Add(new TikTokPerformanceRecord
                    {
                        PublishedLocal=timestamp,
                        Title=Field(title)[..Math.Min(150,Field(title).Length)],
                        Views=ParseMetric(Field(views)),
                        Likes=ParseMetric(Field(likes)),
                        Comments=ParseMetric(Field(comments)),
                        Shares=ParseMetric(Field(shares))
                    });
                }
                if(data.Count==0)throw new InvalidDataException("No usable dated video records found.");
                return data;
            },_cts.Token);
            // Explicit import overwrites previously imported records;
            // the user can always re-import a different export.
            _tiktokIntelligence.Videos=records;
            _tiktokIntelligence.CsvImportedAt=DateTimeOffset.Now;
            SaveTikTokIntelligence();
            TikTokAnalyticsStatus=$"Imported {records.Count} dated TikTok video metrics from CSV. " +
                "Local posting-hour recommendations can now use your historical performance. " +
                "Check the export timezone: dates are treated as your Windows local time.";
        }
        catch(Exception ex)
        {
            TikTokAnalyticsStatus="Analytics CSV import failed: "+ex.Message;
        }
        finally { IntelligenceBusy=false; }
    }

    private async Task CaptureTikTokTrendsAsync()
    {
        if(IntelligenceBusy)return;
        IntelligenceBusy=true;
        try
        {
            await _browser.EnsureStartedAsync(Settings);
            var page=_browser.Context.Pages.LastOrDefault(p=>
                Uri.TryCreate(p.Url,UriKind.Absolute,out var u) &&
                u.Host.Equals("ads.tiktok.com",StringComparison.OrdinalIgnoreCase) &&
                u.AbsolutePath.Contains("creativecenter",StringComparison.OrdinalIgnoreCase));
            if(page==null)
            {
                TikTokTrendsStatus="No TikTok Creative Center page found in the dedicated browser. " +
                    "Open Creative Center first, select your desired region and trends, then retry.";
                return;
            }
            var text=await page.EvaluateAsync<string>(@"()=>(document.body?.innerText||'').slice(0,35000)");
            var tags=Regex.Matches(text,@"(?<!\w)#[\p{L}\p{N}_]{3,45}\b")
                .Select(m=>m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
            if(tags.Count==0)
            {
                TikTokTrendsStatus="Creative Center has no visible hashtags. " +
                    "Open its Trending Hashtags screen and retry. No trends were saved.";
                return;
            }
            _tiktokIntelligence.CreativeCenterHashtags=tags;
            _tiktokIntelligence.CreativeCenterCapturedAt=DateTimeOffset.Now;
            // Region is not inferred: Creative Center may display a different
            // selected country. Never claim these signals are US or PH trends.
            _tiktokIntelligence.TrendRegion="Selected region in Creative Center (verify there)";
            SaveTikTokIntelligence();
            TikTokTrendsStatus=$"Captured {tags.Count} visible Creative Center hashtags " +
                $"at {DateTime.Now:yyyy-MM-dd HH:mm}. Region: use your selected Creative Center region. " +
                "No popularity rank or growth rate was inferred.";
        }
        catch(Exception ex)
        {
            TikTokTrendsStatus="Could not capture visible Creative Center trends: "+ex.Message;
        }
        finally { IntelligenceBusy=false; }
    }

    private void RefreshTikTokIntelligenceSummary()
    {
        var rows=_tiktokIntelligence.Videos;
        if(rows.Count>0)
        {
            var groups=rows.Where(r=>r.Views>0).GroupBy(r=>r.PublishedLocal.Hour)
                .Select(g=>new
                {
                    Hour=g.Key, Count=g.Count(),
                    Views=g.Average(x=>(double)x.Views),
                    Engagement=g.Average(x=>x.Views>0?
                        (x.Likes+2.0*x.Comments+3.0*x.Shares)/x.Views : 0)
                })
                .Where(g=>g.Count>=2)
                .OrderByDescending(g=>g.Engagement)
                .ThenByDescending(g=>g.Views).Take(5).ToList();
            TikTokInsightsSummary=rows.Count+" videos imported. " +
                (groups.Count==0 ? "Not enough examples at each hour to rank posting times confidently." :
                "Historical higher-engagement posting hours (local): "+
                    string.Join(", ",groups.Select(g=>
                        $"{g.Hour:00}:00 ({g.Count} posts, {g.Engagement:P1} engagement proxy)"))+
                    ". Small samples and topic differences can affect results.");
            TikTokAnalyticsStatus=$"Saved TikTok export: {rows.Count} videos, imported " +
                $"{_tiktokIntelligence.CsvImportedAt:yyyy-MM-dd HH:mm}.";
        }
        else
        {
            TikTokInsightsSummary="No per-video history imported. Smart Schedule uses general time-of-day estimates.";
            if(_tiktokIntelligence.StudioCapturedAt.HasValue)
                TikTokAnalyticsStatus="Read-only TikTok Studio page captured "+
                    $"{_tiktokIntelligence.StudioCapturedAt:yyyy-MM-dd HH:mm}. " +
                    "Import per-video analytics CSV to improve posting-time recommendations.";
        }
        if(_tiktokIntelligence.CreativeCenterCapturedAt.HasValue)
            TikTokTrendsStatus=$"{_tiktokIntelligence.CreativeCenterHashtags.Count} visible Creative Center hashtags " +
                $"captured {_tiktokIntelligence.CreativeCenterCapturedAt:yyyy-MM-dd HH:mm}. " +
                "Refresh the page capture for current trends.";
    }

    private string TikTokSchedulingEvidence()
    {
        var rows=_tiktokIntelligence.Videos;
        if(rows.Count<4)return "TikTok account history unavailable or too small; use general viewing-hour heuristics.";
        var hours=rows.Where(x=>x.Views>0).GroupBy(x=>x.PublishedLocal.Hour)
            .Where(g=>g.Count()>=2)
            .Select(g=>new
            {
                Hour=g.Key,Posts=g.Count(),
                Score=g.Average(r=>(r.Likes+2.0*r.Comments+3.0*r.Shares) /
                    Math.Max(1.0,r.Views))
            })
            .OrderByDescending(g=>g.Score).Take(6).ToList();
        if(hours.Count==0)return "Historical analytics sample is too small for posting-hour ranking.";
        return "User-authorized TikTok CSV history ("+rows.Count+
            " videos, imported "+_tiktokIntelligence.CsvImportedAt?.ToString("yyyy-MM-dd")+
            "). Historical posting-hour engagement proxy rankings: "+
            string.Join("; ",hours.Select(x=>
                $"{x.Hour:00}:00 with {x.Posts} posts and {x.Score:P1} engagement proxy"))+
            ". Treat this as correlation, not proof that posting at that hour causes more views.";
    }

    private List<string> TikTokIdeaSignals()
    {
        if(!_tiktokIntelligence.CreativeCenterCapturedAt.HasValue ||
           _tiktokIntelligence.CreativeCenterCapturedAt.Value<DateTimeOffset.Now.AddDays(-3))
            return [];
        return _tiktokIntelligence.CreativeCenterHashtags.Take(20)
            .Select(x=>"TikTok Creative Center visible hashtag ("+
                _tiktokIntelligence.CreativeCenterCapturedAt.Value.ToString("yyyy-MM-dd")+
                ", selected region unverified): "+x).ToList();
    }

    private string TikTokIdeaAccountEvidence()
    {
        var top=_tiktokIntelligence.Videos.Where(x=>x.Views>0)
            .OrderByDescending(x=>x.Views).Take(8).ToList();
        return top.Count==0 ?
            "No per-video TikTok account analytics imported." :
            "Account historical higher-viewed videos (not necessarily trending): "+
            string.Join(" | ",top.Select(x=>x.Title[..Math.Min(90,x.Title.Length)]+
                " — "+x.Views+" views"));
    }
}
