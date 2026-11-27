using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZeroVideo.App.Services;

/// <summary>
/// Compose three independent story beats into one intentionally structured
/// vintage comic page. This prevents a text-to-image model from inventing
/// inconsistent panel borders, fake lettering, or dozens of unreadable panels.
/// The source PNGs are retained as project assets.
/// </summary>
public static class VintageComicPageService
{
    public static void Compose(IReadOnlyList<string> panels,string output,
        int width,int height)
    {
        if(panels.Count!=3||panels.Any(p=>!File.Exists(p)))
            throw new InvalidDataException("Three completed panel PNGs are required.");
        if(Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(()=>ComposeOnUiThread(panels,output,width,height));
            return;
        }
        ComposeOnUiThread(panels,output,width,height);
    }

    private static void ComposeOnUiThread(IReadOnlyList<string> panels,
        string output,int width,int height)
    {
        var border=Math.Max(8,(int)Math.Round(width*0.018));
        var gutter=Math.Max(12,(int)Math.Round(width*0.029));
        var available=height-2*border-2*gutter;
        var heights=new[]
        {
            (int)Math.Round(available*0.37),
            (int)Math.Round(available*0.29),
            0
        };
        heights[2]=available-heights[0]-heights[1];

        var pictures=panels.Select(path=>
        {
            var bitmap=new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption=BitmapCacheOption.OnLoad;
            bitmap.UriSource=new Uri(Path.GetFullPath(path));
            bitmap.EndInit();
            bitmap.Freeze();
            return (ImageSource)bitmap;
        }).ToArray();
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            var background=new SolidColorBrush(Color.FromRgb(23,20,17));
            dc.DrawRectangle(background,null,new Rect(0,0,width,height));
            var y=border;
            for(var i=0;i<3;i++)
            {
                var r=new Rect(border,y,width-2*border,heights[i]);
                var ink=new Pen(new SolidColorBrush(Color.FromRgb(12,11,10)),3.5);
                dc.DrawRectangle(Brushes.Black,ink,r);
                var inner=new Rect(r.X+3,r.Y+3,r.Width-6,r.Height-6);
                dc.PushClip(new RectangleGeometry(inner));
                var source=pictures[i];
                var sx=inner.Width/source.Width;
                var sy=inner.Height/source.Height;
                var scale=Math.Max(sx,sy); // UniformToFill / crop, never distort
                var displayW=source.Width*scale;
                var displayH=source.Height*scale;
                var imageRect=new Rect(
                    inner.Left+(inner.Width-displayW)/2,
                    inner.Top+(inner.Height-displayH)/2,displayW,displayH);
                dc.DrawImage(source,imageRect);
                dc.Pop();
                dc.DrawRectangle(null,new Pen(Brushes.Black,4),inner);
                y+=(int)r.Height+gutter;
            }
        }
        var bitmapOutput=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        bitmapOutput.Render(visual);
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapOutput));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var pending=output+".partial";
        using(var stream=File.Create(pending))encoder.Save(stream);
        File.Move(pending,output,true);
    }
}
