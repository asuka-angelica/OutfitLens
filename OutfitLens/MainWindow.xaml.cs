using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;

namespace OutfitLens;

public partial class MainWindow : Window
{
    readonly ObservableCollection<OutfitItem> items = [];
    string? basePath;
    Bitmap? rendered;
    string placement = "Right";

    public MainWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = items;
    }

    void ChooseBase_Click(object sender, RoutedEventArgs e)
    {
        var d = Picker(false); if (d.ShowDialog()!=true) return;
        basePath = d.FileName; EmptyPreview.Visibility = Visibility.Collapsed; Render();
    }

    async void ChooseWardrobe_Click(object sender, RoutedEventArgs e)
    {
        var d = Picker(true); if (d.ShowDialog()!=true) return;
        foreach (var path in d.FileNames) await AddWardrobe(path);
        Render();
        Status.Text = $"{items.Count}件の衣装を検出しました";
    }

    static OpenFileDialog Picker(bool multi) => new()
    { Filter="画像ファイル|*.png;*.jpg;*.jpeg;*.webp", Multiselect=multi };

    async Task AddWardrobe(string path)
    {
        using var src = new Bitmap(path);
        await AddWardrobe(src);
    }

    async Task AddWardrobe(Bitmap src)
    {
        var part = await DetectPart(src);
        var name = await DetectItemName(src, part);
        var iconRect = DetectIconRect(src,part);
        var icon = src.Clone(iconRect, PixelFormat.Format32bppArgb);
        var iconSource = ToSource(icon);
        var existing = items.FirstOrDefault(x => x.Part == part);
        if (existing != null) items.Remove(existing);
        items.Add(new OutfitItem { Part=part, Name=name ?? "名称を確認してください", IconBitmap=icon, Icon=iconSource });
    }

    async void PasteWardrobe_Click(object sender, RoutedEventArgs e) => await PasteWardrobe();
    async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key==Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { await PasteWardrobe(); e.Handled=true; }
    }

    async Task PasteWardrobe()
    {
        if(!System.Windows.Clipboard.ContainsImage())
        {
            Status.Text="クリップボードに画像がありません"; return;
        }
        var source=System.Windows.Clipboard.GetImage();
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var ms=new MemoryStream();encoder.Save(ms);ms.Position=0;
        using var bitmap=new Bitmap(ms);await AddWardrobe(bitmap);Render();
        Status.Text=$"クリップボードから追加しました（{items.Count}件）";
    }

    static async Task<string> DetectPart(Bitmap b)
    {
        var titlePart=await DetectTitlePart(b);
        int clientTop=DetectClientTop(b);
        float sy=(b.Height-clientTop)/1080f, sx=b.Width/1920f;
        var selectionY=FindSelectionY(b,sx,sy,clientTop);
        var selectionCenter=SelectedSlotCenter(selectionY,sy,clientTop);

        // 頭飾りには2つの装備枠がある。画面タイトルはどちらも「頭飾り」
        // なので、左側の選択枠位置を併用して別々の部位として扱う。
        if(titlePart=="頭飾り" && selectionCenter==clientTop+(int)(246*sy))
            return "頭飾り2";
        if(titlePart!=null) return titlePart;

        // 左側の選択枠位置から部位を判定。1920×1080以外も比率補正。
        bool accessories = Brightness(b,(int)(70*sx),clientTop+(int)(238*sy)) > Brightness(b,(int)(70*sx),clientTop+(int)(150*sy));
        var ys = accessories ? new Dictionary<int,string>{{164,"頭飾り"},{246,"髪飾り"},{328,"顔飾り"},{410,"唇飾り"},{492,"背飾り"},{574,"尻尾飾り"},{656,"耳飾り"},{738,"首飾り"},{820,"指輪"}}
                               : new Dictionary<int,string>{{164,"セット"},{246,"トップス"},{328,"ボトムス"},{410,"ガントレット"},{492,"靴"}};
        int best=ys.Keys.MinBy(y => Math.Abs(clientTop+y*sy-selectionY));
        return ys[best];
    }

    static async Task<string?> DetectTitlePart(Bitmap source)
    {
        try
        {
            int clientTop=DetectClientTop(source);
            int clientHeight=source.Height-clientTop;
            int w=Math.Min(source.Width,Math.Max(1,(int)(520*source.Width/1920f)));
            int h=Math.Min(clientHeight,Math.Max(1,(int)(100*clientHeight/1080f)));
            using var crop=new Bitmap(1560,300,PixelFormat.Format32bppArgb);
            using(var graphics=Graphics.FromImage(crop))
            {
                graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source,new Rectangle(0,0,crop.Width,crop.Height),new Rectangle(0,clientTop,w,h),GraphicsUnit.Pixel);
            }
            using var memory=new InMemoryRandomAccessStream();
            using var output=memory.AsStreamForWrite();
            crop.Save(output,ImageFormat.Png);
            await output.FlushAsync();
            memory.Seek(0);
            var decoder=await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(memory);
            using var software=await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied);
            OcrEngine? engine=null;
            try { engine=OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja-JP")); }
            catch { }
            engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            if(engine==null) return null;
            var result=await engine.RecognizeAsync(software);
            string text=result.Text;
            text=text.Replace(" ",string.Empty);
            text=text.Replace("　",string.Empty);
            text=text.Replace("\r",string.Empty);
            text=text.Replace("\n",string.Empty);
            foreach(var part in OutfitItem.AllParts.OrderByDescending(x=>x.Length))
                if(text.Contains(part,StringComparison.OrdinalIgnoreCase)) return part;
            var suffix=text.Contains('/')?text[(text.LastIndexOf('/')+1)..]:text;
            suffix=suffix.Trim('@','・','：',':');
            var fuzzy=OutfitItem.AllParts
                .Select(part=>(part,distance:EditDistance(suffix,part)))
                .OrderBy(x=>x.distance).First();
            if(fuzzy.distance<=Math.Max(1,fuzzy.part.Length/3)) return fuzzy.part;
            return null;
        }
        catch { return null; }
    }

    static async Task<string?> DetectItemName(Bitmap source,string part)
    {
        try
        {
            int clientTop=DetectClientTop(source);
            int clientHeight=source.Height-clientTop;
            float sx=source.Width/1920f,sy=clientHeight/1080f;
            var sourceRect=new Rectangle(
                Math.Clamp((int)(1540*sx),0,source.Width-1),
                Math.Clamp(clientTop+(int)(710*sy),0,source.Height-1),
                Math.Max(1,Math.Min((int)(375*sx),source.Width-(int)(1540*sx))),
                Math.Max(1,Math.Min((int)(115*sy),source.Height-clientTop-(int)(710*sy))));

            using var crop=new Bitmap(2000,614,PixelFormat.Format32bppArgb);
            using(var graphics=Graphics.FromImage(crop))
            {
                graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source,new Rectangle(0,0,crop.Width,crop.Height),sourceRect,GraphicsUnit.Pixel);
            }
            using var memory=new InMemoryRandomAccessStream();
            using var output=memory.AsStreamForWrite();
            crop.Save(output,ImageFormat.Png);
            await output.FlushAsync();
            memory.Seek(0);
            var decoder=await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(memory);
            using var software=await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied);
            OcrEngine? engine=null;
            try { engine=OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja-JP")); }
            catch { }
            engine ??=OcrEngine.TryCreateFromUserProfileLanguages();
            if(engine==null) return null;
            var result=await engine.RecognizeAsync(software);
            string basePart=part=="頭飾り2"?"頭飾り":part;
            var lines=result.Lines
                .Select(line=>line.Text.Replace(" ",string.Empty).Replace("　",string.Empty).Trim())
                .Where(line=>line.Length>1)
                .ToList();
            string? candidate=lines
                .Where(line=>line.Contains(basePart,StringComparison.Ordinal))
                .OrderByDescending(line=>line.Length)
                .FirstOrDefault();
            if(candidate==null)
            {
                // 長い衣装名はゲームUI上で2行以上に折り返される。
                // OCRの行を表示順に連結し、部位名の末尾までを復元する。
                string joined=string.Concat(lines);
                int end=joined.IndexOf(basePart,StringComparison.Ordinal);
                if(end>=0)
                {
                    candidate=joined[..(end+basePart.Length)];
                }
                else
                {
                    // 改行後が1文字だけの場合、OCRが末尾文字を落とすことがある。
                    // 「・」以降が部位名に近ければ、既知の正式部位名で補完する。
                    joined=Regex.Replace(joined,@"[☆★◇◆]?\d+$",string.Empty);
                    int separator=joined.LastIndexOf('・');
                    if(separator>=0)
                    {
                        string detectedSuffix=joined[(separator+1)..];
                        if(EditDistance(detectedSuffix,basePart)<=Math.Max(2,basePart.Length/2))
                            candidate=joined[..(separator+1)]+basePart;
                    }
                    // 一部の衣装名には「・トップス」などの部位接尾辞がない。
                    // 名前欄だけに限定したOCR領域なので、星数を除いた文字列を採用する。
                    candidate??=joined;
                }
            }
            else
            {
                int end=candidate.IndexOf(basePart,StringComparison.Ordinal);
                candidate=candidate[..(end+basePart.Length)];
            }
            candidate=Regex.Replace(candidate,@"^[^\p{L}\p{N}]+",string.Empty);
            candidate=Regex.Replace(candidate,@"[☆★◇◆]?\d+$",string.Empty);
            candidate=candidate.Trim('◇','◆','☆','★','・','|','｜');
            candidate=Regex.Replace(candidate,@"Tシャッ$","Tシャツ",RegexOptions.IgnoreCase);
            candidate=Regex.Replace(candidate,@"グロープ$","グローブ");
            if(candidate.Length<2 || candidate.All(char.IsDigit) ||
               candidate.IndexOfAny([',','，'])>=0 ||
               candidate is "自由染色" or "入手方法" or "保存")
                return null;
            return candidate;
        }
        catch { return null; }
    }

    static int EditDistance(string a,string b)
    {
        var costs=new int[b.Length+1];
        for(int j=0;j<=b.Length;j++) costs[j]=j;
        for(int i=1;i<=a.Length;i++)
        {
            int previous=costs[0];costs[0]=i;
            for(int j=1;j<=b.Length;j++)
            {
                int old=costs[j];
                costs[j]=Math.Min(Math.Min(costs[j]+1,costs[j-1]+1),previous+(a[i-1]==b[j-1]?0:1));
                previous=old;
            }
        }
        return costs[b.Length];
    }

    static int DetectClientTop(Bitmap b)
    {
        int aspectExcess=b.Height-(int)Math.Round(b.Width*9.0/16.0);
        int scaledMaximum=Math.Max(24,(int)(80*b.Width/1920f));
        if(aspectExcess>=8 && aspectExcess<=scaledMaximum)
            return aspectExcess;

        int max=Math.Min(80,Math.Max(20,b.Height/10));
        var samples=new[] { 12,b.Width/4,b.Width/2,b.Width*3/4,Math.Max(12,b.Width-12) };
        (double average,double range) RowStats(int y)
        {
            var values=samples.Select(x=>Brightness(b,x,Math.Clamp(y,0,b.Height-1))).ToArray();
            return (values.Average(),values.Max()-values.Min());
        }
        var first=RowStats(Math.Min(4,b.Height-1));
        if(first.average<0.62 || first.range>0.28) return 0;
        for(int y=12;y<max;y++)
        {
            var row=RowStats(y);
            if(row.average<0.5 || row.range>0.4) return y;
        }
        return 0;
    }

    static int FindSelectionY(Bitmap b,float sx,float sy,int clientTop)
    {
        int x=(int)(165*sx), best=clientTop+(int)(164*sy); double score=-1;
        for(int y=clientTop+(int)(125*sy);y<clientTop+(int)(850*sy);y+=Math.Max(2,(int)(4*sy)))
        {
            double s=Brightness(b,x,y)+Brightness(b,(int)(231*sx),y);
            if(s>score){score=s;best=y;}
        }
        return best;
    }

    static int SelectedSlotCenter(int detectedY,float sy,int clientTop)
    {
        int[] centers=[164,246,328,410,492,574,656,738,820];
        return clientTop+(int)(centers.MinBy(y=>Math.Abs(clientTop+y*sy-detectedY))*sy);
    }

    static Rectangle DetectIconRect(Bitmap b,string part)
    {
        int clientTop=DetectClientTop(b);
        float sx=b.Width/1920f,sy=(b.Height-clientTop)/1080f;
        var centers=new Dictionary<string,int>
        {
            ["セット"]=164,["トップス"]=246,["ボトムス"]=328,["ガントレット"]=410,["靴"]=492,
            ["頭飾り"]=164,["頭飾り2"]=246,["髪飾り"]=246,["顔飾り"]=328,["唇飾り"]=410,
            ["背飾り"]=492,["尻尾飾り"]=574,["耳飾り"]=656,["首飾り"]=738,["指輪"]=820
        };
        int centerY=clientTop+(int)(centers.GetValueOrDefault(part,164)*sy);
        return new Rectangle((int)(165*sx),Math.Max(0,centerY-(int)(32*sy)),Math.Max(1,(int)(67*sx)),Math.Max(1,(int)(67*sy)));
    }

    static double Brightness(Bitmap b,int x,int y)
    { x=Math.Clamp(x,0,b.Width-1);y=Math.Clamp(y,0,b.Height-1);var c=b.GetPixel(x,y);return(c.R+c.G+c.B)/765.0; }

    void Refresh_Click(object sender,RoutedEventArgs e)=>Render();
    void Placement_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PlacementBox?.SelectedItem is System.Windows.Controls.ComboBoxItem c && c.Tag is string tag)
            placement=tag;
        if (IsLoaded) Render();
    }
    void Icons_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Render();
    }
    void RemoveItem_Click(object sender,RoutedEventArgs e){ if(((FrameworkElement)sender).Tag is OutfitItem i){items.Remove(i);i.IconBitmap?.Dispose();Render();} }

    void Render()
    {
        if(basePath==null)return;
        rendered?.Dispose(); using var src=new Bitmap(basePath);
        float scale=Math.Max(.72f,Math.Min(src.Width/1920f,src.Height/1080f));
        int panelW=(int)(520*scale), pad=(int)(30*scale), row=(int)(72*scale), panelH=Math.Max(src.Height,(int)((118+items.Count*72)*scale));
        bool outsideRight=placement=="Right", outsideBottom=placement=="Bottom";
        int bottomH=(int)((130+Math.Ceiling(items.Count/2.0)*78)*scale);
        int outW=src.Width+(outsideRight?panelW:0), outH=src.Height+(outsideBottom?bottomH:0);
        rendered=new Bitmap(outW,outH,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(rendered); g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.FromArgb(250,243,239));g.DrawImage(src,0,0);
        int cardW=outsideBottom?src.Width:panelW;
        int h=outsideRight?panelH:(outsideBottom?bottomH:(int)((106+items.Count*72)*scale));
        int x=outsideRight?src.Width:(outsideBottom?0:placement.EndsWith("Right")?src.Width-panelW-(int)(28*scale):(int)(28*scale));
        int y=outsideBottom?src.Height:(outsideRight?0:placement.StartsWith("Bottom")?src.Height-h-(int)(28*scale):(int)(28*scale));
        using var panel=new SolidBrush(outsideRight||outsideBottom?Color.FromArgb(255,250,244,242):Color.FromArgb(232,250,244,242));
        using var accent=new SolidBrush(Color.FromArgb(255,176,91,116));
        using var white=new SolidBrush(Color.FromArgb(255,70,54,62));using var muted=new SolidBrush(Color.FromArgb(230,132,111,120));
        g.FillRectangle(panel,new Rectangle(x,y,cardW,h));
        using var linePen=new Pen(Color.FromArgb(255,219,188,195),Math.Max(1,2*scale));
        g.DrawLine(linePen,x+pad,y+(int)(76*scale),x+cardW-pad,y+(int)(76*scale));
        g.DrawEllipse(linePen,x+cardW-(int)(76*scale),y+(int)(22*scale),(int)(30*scale),(int)(30*scale));
        using var titleFont=new Font("Yu Gothic UI",18*scale,System.Drawing.FontStyle.Bold);using var partFont=new Font("Yu Gothic UI",10*scale,System.Drawing.FontStyle.Bold);using var nameFont=new Font("Yu Gothic UI",13*scale,System.Drawing.FontStyle.Regular);
        g.DrawString("MY OUTFIT",titleFont,accent,x+pad,y+(int)(22*scale));
        int iy=y+(int)(94*scale), col=0;
        bool showIcons=ShowIconsBox?.IsChecked!=false;
        foreach(var item in items)
        {
            int cellW=outsideBottom?cardW/2:cardW;
            int ix=outsideBottom?x+(col%2)*cellW:x;
            if(outsideBottom && col>0 && col%2==0) iy+=row;
            int size=(int)(48*scale), textX=ix+pad;
            if(showIcons && item.IconBitmap!=null)
            {
                g.DrawImage(item.IconBitmap,new Rectangle(ix+pad,iy,size,size));
                textX+=size+(int)(15*scale);
            }
            g.DrawString(item.Part,partFont,muted,textX,iy);
            g.DrawString(item.Name,nameFont,white,textX,iy+(int)(20*scale));
            if(!outsideBottom)iy+=row; col++;
        }
        Preview.Source=ToSource(rendered); Status.Text="プレビューを更新しました";
    }

    void Export_Click(object sender,RoutedEventArgs e)
    {
        Render(); if(rendered==null){MessageBox.Show("先に通常スクリーンショットを選択してください。");return;}
        var d=new SaveFileDialog{Filter="PNG画像|*.png",FileName=$"outfit_{DateTime.Now:yyyyMMdd_HHmmss}.png"};
        if(d.ShowDialog()==true){rendered.Save(d.FileName,ImageFormat.Png);Status.Text="PNGを保存しました";MessageBox.Show("衣装情報入り画像を保存しました。","Outfit Lens");}
    }

    static BitmapSource ToSource(Bitmap bitmap)
    {
        using var ms=new MemoryStream(); bitmap.Save(ms,ImageFormat.Png);ms.Position=0;
        var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.StreamSource=ms;bi.EndInit();bi.Freeze();return bi;
    }
}

public class OutfitItem
{
    public static readonly string[] AllParts=["セット","トップス","ボトムス","ガントレット","靴","頭飾り","頭飾り2","髪飾り","顔飾り","唇飾り","背飾り","尻尾飾り","耳飾り","首飾り","指輪"];
    public string[] PartOptions => AllParts;
    public string Part {get;set;}="";
    public string Name {get;set;}="";
    public BitmapSource? Icon {get;set;}
    public Bitmap? IconBitmap {get;set;}
}

static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g,Brush brush,Rectangle r,int radius)
    {
        using var p=new GraphicsPath();int d=radius*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();g.FillPath(brush,p);
    }
}
