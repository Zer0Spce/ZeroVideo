using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Generate the artwork using the existing ChatGPT project-image pipeline,
/// then burn a short truthful headline onto the thumbnail with local WPF
/// typography. This guarantees large, correctly spelled text: the text is
/// never delegated to image generation. Does not rerender the video.
/// </summary>
public sealed class ThumbnailArtworkService(ChatGptBrowserService chat)
{
    public async Task<string> RegenerateAsync(
        VideoProject project, ZeroVideoSettings settings,
        Action<string>? log, CancellationToken ct, int variation = 0)
    {
        if(string.IsNullOrWhiteSpace(project.ProjectDirectory) ||
           !Directory.Exists(project.ProjectDirectory))
            throw new DirectoryNotFoundException(
                "The saved ZeroVideo project folder is missing.");
        var portrait=project.AspectRatio=="9:16";
        var ratio=portrait?"9:16 vertical":"16:9 widescreen";
        var title=project.Title;
        var hook=ThumbnailHeadlineService.Hook(title,project.Topic,variation);
        var look=(variation%3) switch
        {
            0=>"One dramatic, recognizable subject with clear cinematic depth.",
            1=>"A distinct three-quarter angle with one striking story-specific prop.",
            _=>"A high-contrast setting or key evidence with one compelling focal subject."
        };
        var chapter=new ProjectChapter{Number=999,Title="Thumbnail"};
        var scene=new ProjectScene
        {
            Number=1,Role="thumbnail",
            ImagePrompt=
                $"Create a completely new {ratio} cover BACKGROUND for the video "+
                $"'{title}', about '{project.Topic}'. "+
                $"Style: {project.Style}. {look} "+
                "One clear dominant subject directly grounded in the title, "+
                "not a generic anonymous detective or unrelated imagery. "+
                "Illustrate the actual subject, mystery, location, evidence, "+
                "or action of THIS story. Use accurate period details and "+
                "distinct staging on every regeneration. Bold contrast, "+
                "strong lighting and expressive original graphic-novel "+
                "composition where the chosen style calls for it. "+
                "Keep the BOTTOM 35 PERCENT of the image visually uncluttered "+
                "and darker to accommodate a VERY LARGE headline that "+
                "ZeroVideo will add afterward. The focal subject should "+
                "remain clearly visible above this lower text-safe zone. "+
                "Do NOT render any typography, labels, letters, caption, "+
                "watermark, logos, or tiny text within the artwork. "+
                "The final title overlay is added separately for perfect spelling."
        };
        ct.ThrowIfCancellationRequested();
        log?.Invoke("Thumbnail: generating fresh "+ratio+
            " artwork for '"+title+"'. Headline: "+hook);
        var generated=await chat.GenerateSceneImageAsync(
            project,chapter,scene,settings,log,ct);
        if(!File.Exists(generated) || new FileInfo(generated).Length<1024)
            throw new IOException(
                "ChatGPT did not return a complete thumbnail background PNG.");
        ct.ThrowIfCancellationRequested();

        var finished=Path.Combine(project.ProjectDirectory,"thumbnail.png");
        var staging=Path.Combine(project.ProjectDirectory,
            ".zerovideo-thumbnail-"+Guid.NewGuid().ToString("N")+".png");
        try
        {
            RenderCaptionedPng(generated,staging,hook,portrait);
            ct.ThrowIfCancellationRequested();
            if(!File.Exists(staging) || new FileInfo(staging).Length<4096)
                throw new IOException("Thumbnail artwork render is incomplete.");
            // Old thumbnail stays intact if image generation, overlay,
            // save or cancellation fails. Replace only after validation.
            File.Move(staging,finished,overwrite:true);
            log?.Invoke("Thumbnail: saved "+(portrait?"1080x1920":"1920x1080")+
                " click-focused image with large, bold '"+hook+
                "' headline: "+finished);
            return finished;
        }
        finally
        {
            try {if(File.Exists(staging))File.Delete(staging);}
            catch(IOException){}
        }
    }

    public static void RenderCaptionedPng(
        string background,string output,string headline,bool portrait)
    {
        if(string.IsNullOrWhiteSpace(headline))
            throw new ArgumentException(
                "A short factual thumbnail headline is required.",nameof(headline));
        BitmapSource image;
        using(var input=File.OpenRead(background))
        {
            var decoder=BitmapDecoder.Create(input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if(decoder.Frames.Count==0)
                throw new InvalidDataException(
                    "Thumbnail background contains no image frames.");
            image=decoder.Frames[0];
            image.Freeze();
        }

        var w=portrait?1080:1920;
        var h=portrait?1920:1080;
        var bounds=new Rect(0,0,w,h);
        var lines=ThumbnailHeadlineService.Lines(headline);
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
        {
            var photo=new ImageBrush(image)
            {
                Stretch=Stretch.UniformToFill,
                AlignmentX=AlignmentX.Center,
                AlignmentY=AlignmentY.Center
            };
            drawing.DrawRectangle(photo,null,bounds);

            // Only a restrained, faded lower shadow. The story image
            // stays visible while the hook is readable at phone size.
            var shade=new LinearGradientBrush
            {
                StartPoint=new Point(0,0),
                EndPoint=new Point(0,1)
            };
            shade.GradientStops.Add(new GradientStop(
                Color.FromArgb(0,0,0,0),0.0));
            shade.GradientStops.Add(new GradientStop(
                Color.FromArgb(185,0,0,0),0.42));
            shade.GradientStops.Add(new GradientStop(
                Color.FromArgb(245,0,0,0),1.0));
            var panelHeight=portrait?h*0.44:h*0.46;
            drawing.DrawRectangle(shade,null,
                new Rect(0,h-panelHeight,w,panelHeight));

            var font=portrait?174.0:162.0;
            var maxWidth=w*(portrait?0.83:0.87);
            var face=new Typeface(new FontFamily("Arial Black"),
                FontStyles.Normal,FontWeights.Black,FontStretches.Normal);
            FormattedText Format(string value,double size) =>
                new(value,CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,face,size,
                    Brushes.White,1.0)
                {
                    TextAlignment=TextAlignment.Center
                };

            // One shared font size for BOTH lines, never a mismatched or
            // curved-letter composition. Shrink only if necessary.
            foreach(var line in lines)
            {
                var measured=Format(line,font);
                if(measured.WidthIncludingTrailingWhitespace>maxWidth)
                    font=Math.Min(font,font*maxWidth/
                        measured.WidthIncludingTrailingWhitespace);
            }
            font=Math.Clamp(font,portrait?82:88,portrait?174:162);
            var samples=lines.Select(line=>Format(line,font)).ToList();
            var spacing=portrait?16.0:10.0;
            var lineHeight=samples.Max(s=>s.Height);
            var total=samples.Count*lineHeight+
                (samples.Count-1)*spacing;
            var bottom=portrait?h*0.078:h*0.055;
            var top=h-bottom-total;
            if(top<h-panelHeight+18)
                top=h-panelHeight+18;
            var outline=new Pen(Brushes.Black,
                Math.Clamp(font*0.095,10,19))
            {
                LineJoin=PenLineJoin.Round
            };
            outline.Freeze();
            var yellow=new SolidColorBrush(
                Color.FromRgb(255,225,51));
            yellow.Freeze();

            for(var i=0;i<samples.Count;i++)
            {
                var line=samples[i];
                var x=(w-line.WidthIncludingTrailingWhitespace)/2;
                var y=top+i*(lineHeight+spacing);
                var glyphs=line.BuildGeometry(new Point(x,y));
                drawing.DrawGeometry(yellow,outline,glyphs);
            }
        }

        var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var png=new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using(var file=File.Create(output))
            png.Save(file);
    }
}
