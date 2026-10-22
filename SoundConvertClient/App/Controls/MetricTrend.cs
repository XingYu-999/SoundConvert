using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SoundConvertClient.Controls;

/// <summary>Bounded one-second telemetry history. Never reads audio devices or sockets.</summary>
public sealed class MetricTrend : Control
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<MetricTrend,string>(nameof(Title), "");
    public static readonly StyledProperty<string> UnitProperty = AvaloniaProperty.Register<MetricTrend,string>(nameof(Unit), "");
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<MetricTrend,double>(nameof(Value));
    public static readonly StyledProperty<double> UpperLimitProperty = AvaloniaProperty.Register<MetricTrend,double>(nameof(UpperLimit), 40);
    public static readonly StyledProperty<bool> SignedProperty = AvaloniaProperty.Register<MetricTrend,bool>(nameof(Signed));
    public static readonly StyledProperty<bool> HasValueProperty = AvaloniaProperty.Register<MetricTrend,bool>(nameof(HasValue));
    public static readonly StyledProperty<long> SampleIdProperty = AvaloniaProperty.Register<MetricTrend,long>(nameof(SampleId));
    public static readonly StyledProperty<long> GenerationProperty = AvaloniaProperty.Register<MetricTrend,long>(nameof(Generation));
    readonly Queue<(long Id,double? Value)> history = new();
    long lastId = -1;
    bool pending;
    public string Title { get=>GetValue(TitleProperty); set=>SetValue(TitleProperty,value); }
    public string Unit { get=>GetValue(UnitProperty); set=>SetValue(UnitProperty,value); }
    public double Value { get=>GetValue(ValueProperty); set=>SetValue(ValueProperty,value); }
    public double UpperLimit { get=>GetValue(UpperLimitProperty); set=>SetValue(UpperLimitProperty,value); }
    public bool Signed { get=>GetValue(SignedProperty); set=>SetValue(SignedProperty,value); }
    public bool HasValue { get=>GetValue(HasValueProperty); set=>SetValue(HasValueProperty,value); }
    public long SampleId { get=>GetValue(SampleIdProperty); set=>SetValue(SampleIdProperty,value); }
    public long Generation { get=>GetValue(GenerationProperty); set=>SetValue(GenerationProperty,value); }
    public int SampleCount => history.Count;
    public IReadOnlyList<double?> Samples => history.Select(p=>p.Value).ToArray();

    static MetricTrend() => AffectsRender<MetricTrend>(TitleProperty,UnitProperty,UpperLimitProperty,SignedProperty,HasValueProperty,ValueProperty);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==GenerationProperty) { history.Clear(); lastId=-1; InvalidateVisual(); }
        if(change.Property==SampleIdProperty && !pending)
        {
            // A snapshot updates several bindings: sample only after all values have arrived.
            pending=true; Dispatcher.UIThread.Post(()=>{pending=false; Append();},DispatcherPriority.Background);
        }
    }
    void Append()
    {
        long id=SampleId;
        if(id<=0||id==lastId)return;
        if(id<lastId)history.Clear();
        history.Enqueue((id,HasValue&&double.IsFinite(Value)?Value:null)); lastId=id;
        while(history.Count>60||history.Count>0&&history.Peek().Id<id-59)history.Dequeue();
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if(Bounds.Width<80||Bounds.Height<55)return;
        var ink=new SolidColorBrush(Color.Parse("#667085"));
        var accent=new SolidColorBrush(Color.Parse(Signed?"#8B5CF6":"#1677FF"));
        var font=new Typeface("Microsoft YaHei UI, Segoe UI");
        FormattedText Text(string s,double size,IBrush brush)=>new(s,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,font,size,brush);
        context.DrawText(Text(Title,12,ink),new(0,0));
        string readout=HasValue&&double.IsFinite(Value)?$"{Value:0.0} {Unit}":"等待数据";
        var current=Text(readout,12,accent); context.DrawText(current,new(Math.Max(0,Bounds.Width-current.Width),0));
        var plot=new Rect(1,27,Bounds.Width-2,Bounds.Height-43);
        var values=history.Where(p=>p.Value.HasValue).Select(p=>p.Value!.Value).ToArray();
        double max=Signed?Math.Max(1,values.Select(Math.Abs).DefaultIfEmpty().Max()*1.2):Math.Max(Math.Max(4,UpperLimit),values.DefaultIfEmpty().Max()*1.1);
        if(!double.IsFinite(max)||max<=0)max=1;
        double min=Signed?-max:0;
        double Y(double value)=>plot.Bottom-(value-min)/(max-min)*plot.Height;
        var grid=new Pen(new SolidColorBrush(Color.Parse("#DFE6EF")),1);
        context.DrawLine(grid,plot.TopLeft,plot.TopRight);
        context.DrawLine(grid,new(plot.Left,Y(0)),new(plot.Right,Y(0)));
        context.DrawLine(grid,plot.BottomLeft,plot.BottomRight);
        context.DrawText(Text($"{max:0.#}",10,ink),new(plot.Left,plot.Top+1));
        if(Signed)
        {
            context.DrawText(Text("0",10,ink),new(plot.Left,Y(0)-13));
            context.DrawText(Text($"{min:0.#}",10,ink),new(plot.Left,plot.Bottom-13));
        }
        context.DrawText(Text("60 秒前",10,ink),new(0,Bounds.Height-14));
        var now=Text("现在",10,ink); context.DrawText(now,new(Bounds.Width-now.Width,Bounds.Height-14));
        if(history.Count==0)return;
        var geometry=new StreamGeometry(); bool drawing=false;long previous=-1;
        Point last=default; bool havePoint=false;
        using(var path=geometry.Open())
        {
            foreach(var sample in history)
            {
                if(sample.Value is not double value) { if(drawing)path.EndFigure(false); drawing=false; previous=sample.Id; continue; }
                var point=new Point(plot.Right-(lastId-sample.Id)/59d*plot.Width,Y(value));
                if(!drawing||sample.Id!=previous+1) { if(drawing)path.EndFigure(false);path.BeginFigure(point,false); } else path.LineTo(point);
                drawing=true; previous=sample.Id; last=point;havePoint=true;
            }
            if(drawing)path.EndFigure(false);
        }
        context.DrawGeometry(null,new Pen(accent,1.8),geometry);
        if(havePoint&&HasValue)context.DrawEllipse(accent,null,last,2.5,2.5);
    }
}
