using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("声线工作室")]
[assembly: AssemblyDescription("GPT-SoVITS 本地桌面工作流")]
[assembly: AssemblyVersion("1.2.1.0")]

namespace VoiceStudio {
 public class Clip : INotifyPropertyChanged {
  bool keep = true; string text = "";
  public bool Use { get { return keep; } set { keep=value; Changed("Use"); } }
  public string Name { get;set; } public string Path {get;set;} public double Duration {get;set;}
  public string Text {get{return text;}set{text=value;Changed("Text");}}
  public event PropertyChangedEventHandler PropertyChanged;
  void Changed(string name){if(PropertyChanged!=null)PropertyChanged(this,new PropertyChangedEventArgs(name));}
 }
 public class Project {
  public string Name {get;set;} public string Directory {get;set;} public List<string> Sources {get;set;}
  public List<Clip> Clips {get;set;} public Dictionary<string,object> Trained {get;set;}
  public string ReferencePath {get;set;} public string ReferenceText {get;set;} public string TargetText {get;set;}
  public string LastOutput {get;set;}
  public int RefLanguage {get;set;} public int TextLanguage {get;set;} public double Speed {get;set;} public bool UseTrained {get;set;}
  public Project(){Name="我的声音";Directory="";Sources=new List<string>();Clips=new List<Clip>();ReferencePath="";ReferenceText="";TargetText="";LastOutput="";Speed=1;}
 }
 public static class ReferenceDefaults {
  public static List<Clip> Eligible(IEnumerable<Clip> clips){return clips.Where(c=>c.Use && c.Duration>=3 && c.Duration<=10 && !string.IsNullOrWhiteSpace(c.Text) && File.Exists(c.Path)).OrderBy(c=>Math.Abs(c.Duration-6)).ThenBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).ToList();}
  public static bool Apply(Project project,IEnumerable<Clip> clips){
   var all=clips.ToList();Clip current=all.FirstOrDefault(c=>string.Equals(c.Path,project.ReferencePath,StringComparison.OrdinalIgnoreCase));
   if(File.Exists(project.ReferencePath)){
    if(current==null)return false; // Preserve an explicitly chosen external recording.
    if(current.Use && current.Duration>=3 && current.Duration<=10 && (!string.IsNullOrWhiteSpace(project.ReferenceText)||!string.IsNullOrWhiteSpace(current.Text))){
     if(string.IsNullOrWhiteSpace(project.ReferenceText)){project.ReferenceText=current.Text;project.RefLanguage=0;return true;}return false;
    }
   }
   Clip selected=Eligible(all).FirstOrDefault();if(selected==null)return false;
   project.ReferencePath=selected.Path;project.ReferenceText=selected.Text;project.RefLanguage=0;return true;
  }
 }
 public class Studio {
  readonly JavaScriptSerializer json=new JavaScriptSerializer(){MaxJsonLength=32*1024*1024};
  readonly string appDir=AppDomain.CurrentDomain.BaseDirectory;
  readonly Window window; readonly StackPanel host,nav; readonly TextBlock title,subtitle,status,caption,badge;
  readonly TextBox logs; readonly ProgressBar progress; readonly Button cancel;
  readonly MediaPlayer player=new MediaPlayer(); readonly bool smoke;
  Project project=new Project(); ObservableCollection<Clip> clips=new ObservableCollection<Clip>();
  string engine="",errorMessage=""; int page=0; bool busy=false,cancelled=false,jobSucceeded=false;
  Process running; Dictionary<string,object> result; readonly List<Button> navButtons=new List<Button>();
  CancellationTokenSource fileCancellation; bool closingAfterCancel=false;
  TextBox nameBox,referenceBox,referenceText,targetText,sovitsEpochs,gptEpochs,batchBox;
  DateTime taskStarted;
  ComboBox trainVersion,voiceMode,refLanguage,textLanguage; Slider speed;
  DataGrid grid; TextBlock clipSummary; DispatcherTimer autosave;
  static readonly string[] Labels={"选择素材","整理片段","校对文字","训练声音","生成配音"};
  static readonly string[] Titles={"从一段视频或录音开始","挑出你想保留的声音","让文字与录音逐句对应","训练一个专属声音","让这个声音，说出新的内容"};
  static readonly string[] Descriptions={"选择本地文件，程序会读取音轨。可以一次选择多个文件，也可以直接拖入。","逐条试听，只保留目标人物单独说话的片段。取消勾选不会删除原文件。","先自动识别普通话，再试听校对。双击文字即可修改，空白文字不会被用于训练。","已校对的片段会自动完成特征准备、音色训练和语音表达训练。","可以直接用一段参考录音体验，也可以使用这个项目训练出的声音。"};
  public Studio(bool smokeMode){
   smoke=smokeMode;
   using(Stream resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")){window=(Window)XamlReader.Load(resource);}
   host=Find<StackPanel>("PageHost");nav=Find<StackPanel>("Nav");title=Find<TextBlock>("PageTitle");subtitle=Find<TextBlock>("PageSubtitle");status=Find<TextBlock>("StatusText");caption=Find<TextBlock>("ProjectCaption");badge=Find<TextBlock>("EngineBadge");logs=Find<TextBox>("Logs");progress=Find<ProgressBar>("Progress");cancel=Find<Button>("CancelButton");
   for(int i=0;i<5;i++){int index=i; Button b=new Button(){Content=string.Format("{0:00}    {1}",i+1,Labels[i]),HorizontalContentAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,8),Padding=new Thickness(15,14,8,14),FontSize=14,BorderThickness=new Thickness(0)};b.Click+=(s,e)=>Go(index);nav.Children.Add(b);navButtons.Add(b);}
   Find<Button>("NewButton").Click+=(s,e)=>{if(busy){Notice("请先等待或停止当前任务。");return;}SyncDraft();SaveProject();project=new Project();clips=new ObservableCollection<Clip>();Go(0);};
   Find<Button>("OpenButton").Click+=(s,e)=>OpenProject();
   Find<Button>("FolderButton").Click+=(s,e)=>{EnsureProject();OpenFolder(project.Directory);};
   Find<Button>("SettingsButton").Click+=(s,e)=>ChooseEngine();
   cancel.Click+=async(s,e)=>{if(fileCancellation!=null){cancelled=true;fileCancellation.Cancel();status.Text="正在停止当前任务…";return;}if(running==null)return;cancelled=true;status.Text="正在停止当前任务…";await Task.Run(()=>KillOwnProcess());};
   window.Closing+=async(s,e)=>{if(closingAfterCancel){e.Cancel=true;return;}if(busy && MessageBox.Show(window,"关闭窗口会停止当前任务。确认关闭？","声线工作室",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes){e.Cancel=true;return;}if(fileCancellation!=null){e.Cancel=true;closingAfterCancel=true;cancelled=true;fileCancellation.Cancel();while(busy)await Task.Delay(100);closingAfterCancel=false;window.Close();return;}cancelled=true;SyncDraft();SaveProject();KillOwnProcess();player.Close();};
   LoadSettings();
   if(string.IsNullOrEmpty(engine))engine=DetectEngine();
   badge.Text=ValidEngine()?"语音引擎已找到\n视频、录音在本机处理":"需要选择语音引擎文件夹";
   Go(project.Trained!=null && project.UseTrained?4:clips.Count>0?2:0);
   autosave=new DispatcherTimer(){Interval=TimeSpan.FromSeconds(3)};
   autosave.Tick+=(s,e)=>{SyncDraft();if(!busy)SaveProject();else Find<TextBlock>("TaskTime").Text="已运行 "+(DateTime.Now-taskStarted).ToString(@"hh\:mm\:ss");UpdateSummary();};
   if(!smoke)autosave.Start();
  }
  T Find<T>(string name) where T:class{return window.FindName(name) as T;}
  bool ValidEngine(){return File.Exists(Path.Combine(engine,"runtime","python.exe")) && File.Exists(Path.Combine(engine,"api_v2.py"));}
  string DetectEngine(){
   var folder=new DirectoryInfo(appDir);
   for(int level=0;folder!=null && level<3;level++,folder=folder.Parent){
    string root=folder.FullName;
    if(File.Exists(Path.Combine(root,"runtime","python.exe")) && File.Exists(Path.Combine(root,"api_v2.py")))return root;
    try{foreach(string d in System.IO.Directory.GetDirectories(root,"GPT-SoVITS*")){if(File.Exists(Path.Combine(d,"runtime","python.exe")) && File.Exists(Path.Combine(d,"api_v2.py")))return d;}}
    catch(UnauthorizedAccessException){}catch(IOException){}
   }
   return "";
  }
  void LoadSettings(){try{string f=Path.Combine(appDir,"settings.json");if(!File.Exists(f))return;var d=json.Deserialize<Dictionary<string,string>>(File.ReadAllText(f));engine=d.ContainsKey("engine")?d["engine"]:"";if(d.ContainsKey("last_project") && File.Exists(d["last_project"]))LoadProject(d["last_project"]);}catch(Exception e){Log("设置读取失败："+e.Message);}}
  void Settings(){if(smoke)return;try{AtomicWrite(Path.Combine(appDir,"settings.json"),json.Serialize(new Dictionary<string,string>{{"engine",engine},{"last_project",string.IsNullOrEmpty(project.Directory)?"":Path.Combine(project.Directory,"project.json")}}));}catch(Exception e){Log(e.Message);}}
  void ChooseEngine(){if(busy){Notice("请先等待或停止当前任务。");return;}using(var picker=new Forms.FolderBrowserDialog()){picker.Description="选择包含 runtime、GPT_SoVITS 和 api_v2.py 的整合包文件夹";picker.SelectedPath=engine;if(picker.ShowDialog()!=Forms.DialogResult.OK)return;string before=engine;engine=picker.SelectedPath;if(!ValidEngine()){engine=before;Notice("这个文件夹中没有完整的 GPT-SoVITS 引擎，请选择解压后的整合包根目录。");return;}Settings();badge.Text="语音引擎已找到\n视频、录音在本机处理";status.Text="引擎位置已保存。";}}
  void EnsureProject(){if(!string.IsNullOrEmpty(project.Directory))return;string baseDir=Path.GetDirectoryName(appDir.TrimEnd(Path.DirectorySeparatorChar));if(string.IsNullOrEmpty(baseDir))baseDir=appDir;string folder=DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N").Substring(0,5);project.Directory=Path.Combine(baseDir,"Projects",folder);System.IO.Directory.CreateDirectory(project.Directory);SaveProject();Settings();}
  static void AtomicWrite(string path,string value){string temp=path+".tmp";File.WriteAllText(temp,value,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
  void SyncDraft(){if(nameBox!=null && page==0){project.Name=string.IsNullOrWhiteSpace(nameBox.Text)?"我的声音":nameBox.Text.Trim();caption.Text=project.Name;}if(page==4){if(referenceBox!=null)project.ReferencePath=referenceBox.Text;if(referenceText!=null)project.ReferenceText=referenceText.Text;if(targetText!=null)project.TargetText=targetText.Text;if(refLanguage!=null)project.RefLanguage=refLanguage.SelectedIndex;if(textLanguage!=null)project.TextLanguage=textLanguage.SelectedIndex;if(speed!=null)project.Speed=speed.Value;if(voiceMode!=null)project.UseTrained=voiceMode.SelectedIndex==1;}}
  void SaveProject(){if(smoke || string.IsNullOrEmpty(project.Directory))return;try{project.Clips=clips.ToList();AtomicWrite(Path.Combine(project.Directory,"project.json"),json.Serialize(project));}catch(Exception e){Log("项目保存失败："+e.Message);}}
  void LoadProject(string path){project=json.Deserialize<Project>(File.ReadAllText(path,Encoding.UTF8));if(project==null)throw new Exception("项目文件为空");project.Directory=Path.GetDirectoryName(Path.GetFullPath(path));if(project.Sources==null)project.Sources=new List<string>();clips=new ObservableCollection<Clip>(project.Clips??new List<Clip>());page=-1;}
  void OpenProject(){if(busy){Notice("请先等待或停止当前任务。");return;}OpenFileDialog d=new OpenFileDialog(){Title="打开声音项目",Filter="声音项目 (project.json)|project.json|JSON 文件|*.json"};if(d.ShowDialog(window)!=true)return;try{SyncDraft();SaveProject();LoadProject(d.FileName);Settings();Go(project.Trained!=null && project.UseTrained?4:clips.Count>0?2:0);}catch(Exception e){Notice("项目读取失败："+e.Message);}}
  void Notice(string message){if(smoke){errorMessage=message;Log(message);return;}MessageBox.Show(window,message,"声线工作室",MessageBoxButton.OK,MessageBoxImage.Information);}
  void Log(string text){if(string.IsNullOrEmpty(text))return;if(logs.Text.Length>140000)logs.Text=logs.Text.Substring(logs.Text.Length-90000);logs.AppendText(text+Environment.NewLine);logs.ScrollToEnd();}
  static Brush Color(string value){return (Brush)new BrushConverter().ConvertFromString(value);}
  TextBlock Text(string value,double size=14,string color="#526880"){return new TextBlock(){Text=value,FontSize=size,Foreground=Color(color),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10),LineHeight=size*1.65};}
  TextBox Input(string value,bool multiline=false,int height=42){return new TextBox(){Text=value??"",AcceptsReturn=multiline,TextWrapping=multiline?TextWrapping.Wrap:TextWrapping.NoWrap,Height=height,VerticalScrollBarVisibility=multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Hidden,Margin=new Thickness(0,0,0,16),VerticalContentAlignment=multiline?VerticalAlignment.Top:VerticalAlignment.Center};}
  Button Button(string label,Action action,bool primary=false){Button b=new Button(){Content=label};if(primary)b.Style=(Style)window.Resources["Primary"];b.Click+=(s,e)=>action();return b;}
  StackPanel Row(params UIElement[] elements){StackPanel row=new StackPanel(){Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,16)};foreach(var el in elements)row.Children.Add(el);return row;}
  void Label(string value){host.Children.Add(Text(value,13,"#233E60"));}
  void Hint(string value){Border b=new Border(){Background=Color("#F0F5FD"),CornerRadius=new CornerRadius(7),Padding=new Thickness(14),Margin=new Thickness(0,8,0,16)};TextBlock t=Text(value,12);t.Margin=new Thickness(0);b.Child=t;host.Children.Add(b);}
  void Go(int index){
   SyncDraft();SaveProject();player.Stop();page=index;host.Children.Clear();nameBox=null;referenceBox=null;referenceText=null;targetText=null;grid=null;clipSummary=null;
   title.Text=Titles[index];subtitle.Text=Descriptions[index];caption.Text=project.Name;
   for(int i=0;i<5;i++){navButtons[i].Background=Color(i==index?"#E8F0FF":"#FFFFFF");navButtons[i].Foreground=Color(i==index?"#2258BD":"#667D97");navButtons[i].FontWeight=i==index?FontWeights.SemiBold:FontWeights.Normal;}
   if(index==0)PageImport();if(index==1)PageClips(false);if(index==2)PageClips(true);if(index==3)PageTrain();if(index==4)PageGenerate();host.IsEnabled=!busy;
  }
  void PageImport(){
   Label("声音项目名称");nameBox=Input(project.Name);host.Children.Add(nameBox);
   Border drop=new Border(){Background=Color("#F5F8FF"),BorderBrush=Color("#B8CDF2"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(28),Margin=new Thickness(0,0,0,18),AllowDrop=true};
   StackPanel content=new StackPanel();content.Children.Add(Text("选择视频或录音",21,"#254B81"));content.Children.Add(Text("MP4、MKV、MOV、WAV、MP3、M4A、FLAC\n支持多选，也可以把文件拖到这里。",13));content.Children.Add(Row(Button("浏览文件…",PickSources,true)));drop.Child=content;
   drop.DragOver+=(s,e)=>{e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
   drop.Drop+=(s,e)=>{if(busy)return;string[] paths=e.Data.GetData(DataFormats.FileDrop) as string[];if(paths!=null)AddSources(paths);};host.Children.Add(drop);
   if(project.Sources.Count>0){host.Children.Add(Text("已选择 "+project.Sources.Count+" 个文件",13,"#167B73"));ListBox list=new ListBox(){MaxHeight=130,Margin=new Thickness(0,0,0,15),BorderBrush=Color("#DBE4EF")};foreach(var p in project.Sources)list.Items.Add(p);host.Children.Add(list);host.Children.Add(Row(Button("整理录音片段",()=>RunJob("prepare",null),true),Button("清空选择",()=>{project.Sources.Clear();Go(0);})));}
   Hint("先挑选目标人物单独说话、声音清楚的素材。程序会读取音轨并分成短片段，之后可以逐条试听和筛选。");
   host.Children.Add(Button("已有参考录音？直接去生成配音 →",()=>Go(4)));
  }
  void PickSources(){OpenFileDialog d=new OpenFileDialog(){Title="选择视频或录音，可按 Ctrl 多选",Multiselect=true,Filter="视频和录音|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wav;*.mp3;*.m4a;*.flac;*.aac;*.ogg;*.wma|所有文件|*.*"};if(d.ShowDialog(window)==true)AddSources(d.FileNames);}
  void AddSources(IEnumerable<string> paths){SyncDraft();foreach(string p in paths){if(File.Exists(p) && !project.Sources.Contains(p))project.Sources.Add(p);}EnsureProject();Go(0);}
  void PageClips(bool edit){
   if(clips.Count==0){Hint("这里还没有录音片段。请先选择视频或录音，再点击“整理录音片段”。");host.Children.Add(Button("选择素材",()=>Go(0),true));return;}
   clipSummary=Text("",15,"#167B73");host.Children.Add(clipSummary);
   grid=new DataGrid(){ItemsSource=clips,Height=edit?300:290,SelectionMode=DataGridSelectionMode.Single,Margin=new Thickness(0,0,0,15)};
   var check=new FrameworkElementFactory(typeof(CheckBox));check.SetBinding(CheckBox.IsCheckedProperty,new Binding("Use"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});check.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);check.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
   grid.Columns.Add(new DataGridTemplateColumn(){Header="保留",CellTemplate=new DataTemplate(){VisualTree=check},Width=64});
   grid.Columns.Add(new DataGridTextColumn(){Header="录音片段",Binding=new Binding("Name"),IsReadOnly=true,Width=edit?140:240});
   grid.Columns.Add(new DataGridTextColumn(){Header="时长",Binding=new Binding("Duration"){StringFormat="{0:0.0} 秒"},IsReadOnly=true,Width=85});
   if(edit){var col=new DataGridTextColumn(){Header="录音中说出的文字（双击修改）",Binding=new Binding("Text"){UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=new DataGridLength(1,DataGridLengthUnitType.Star)};Style ts=new Style(typeof(TextBlock));ts.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));ts.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Center));col.ElementStyle=ts;grid.Columns.Add(col);}
   else grid.Columns.Add(new DataGridTextColumn(){Header="文字",Binding=new Binding("Text"),IsReadOnly=true,Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
   DataGrid currentGrid=grid;currentGrid.SizeChanged+=(s,e)=>{if(currentGrid.Columns.Count==4)currentGrid.Columns[3].Width=new DataGridLength(Math.Max(260,currentGrid.ActualWidth-(edit?140:240)-64-85-24));};
   host.Children.Add(grid);
   host.Children.Add(Row(Button("▶ 试听选中片段",()=>PlaySelected()),Button("停止播放",()=>player.Stop()),Button("用作参考录音",()=>UseReference())));
   if(edit){host.Children.Add(Row(Button("自动识别普通话",()=>RunJob("transcribe",null),true),Button("保存校对",()=>{grid.CommitEdit();grid.CommitEdit();SaveProject();status.Text="校对内容已保存。";}),Button("下一步：训练声音 →",()=>Go(3))));Hint("识别后请逐条检查人名、数字和专业词语。听不清、其他人说话或有重叠人声的片段，取消“保留”即可。");}
   else{host.Children.Add(Row(Button("下一步：识别与校对 →",()=>Go(2),true),Button("打开录音文件夹",()=>{if(clips.Count>0)OpenFolder(Path.GetDirectoryName(clips[0].Path));})));Hint("先点击一条录音再试听。勾选框决定哪些片段会进入后续训练，原始视频不会被改动。");}
   UpdateSummary();
  }
  void UpdateSummary(){if(clipSummary==null)return;var selected=clips.Where(c=>c.Use).ToList();int missing=selected.Count(c=>string.IsNullOrWhiteSpace(c.Text));clipSummary.Text=string.Format("保留 {0} / {1} 条  ·  有效录音 {2:0.0} 分钟{3}",selected.Count,clips.Count,selected.Sum(c=>c.Duration)/60,page==2?"  ·  待填写文字 "+missing+" 条":"");}
  void PlaySelected(){Clip c=grid==null?null:grid.SelectedItem as Clip;if(c==null){Notice("请先在列表中选择一条录音。");return;}Play(c.Path);}
  void Play(string path){if(!File.Exists(path)){Notice("音频文件不存在，请重新选择。");return;}player.Open(new Uri(Path.GetFullPath(path)));player.Play();status.Text="正在试听："+Path.GetFileName(path);}
  void UseReference(){Clip c=grid==null?null:grid.SelectedItem as Clip;if(c==null){Notice("请先选择一条 3～10 秒的录音。");return;}if(c.Duration<3 || c.Duration>10){Notice("参考录音需要在 3～10 秒之间，请换一条。");return;}project.ReferencePath=c.Path;project.ReferenceText=c.Text??"";SaveProject();Go(4);}
  void PageTrain(){
   var selected=clips.Where(c=>c.Use).ToList();int missing=selected.Count(c=>string.IsNullOrWhiteSpace(c.Text));
   host.Children.Add(Text(string.Format("已保留 {0} 条录音，共 {1:0.0} 分钟",selected.Count,selected.Sum(c=>c.Duration)/60),20,"#233E60"));
   host.Children.Add(Text(missing==0 && selected.Count>=2?"文字已准备好，可以开始训练。":"请先完成录音筛选和文字校对。至少需要两条带有文字的录音。",13,missing==0 && selected.Count>=2?"#167B73":"#A56B24"));
   Label("训练方式");trainVersion=new ComboBox(){Width=370,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,16)};trainVersion.Items.Add("V2Pro · 日常录音与视频素材（推荐）");trainVersion.Items.Add("V4 · 高音质、清晰的录音素材");trainVersion.SelectedIndex=0;host.Children.Add(trainVersion);
   Hint("点击一次，依次完成：准备特征 → 训练音色 → 训练语音表达。过程会使用显卡，进度和当前步骤会显示在下方。");
   Expander advanced=new Expander(){Header="调整训练参数",Margin=new Thickness(0,0,0,20)};StackPanel settings=new StackPanel(){Margin=new Thickness(0,12,0,0)};
   settings.Children.Add(Text("首次训练可以使用默认值。显存不足时，将批次大小改为 2。",12));
   sovitsEpochs=Input("8");gptEpochs=Input("15");batchBox=Input("4");
   settings.Children.Add(Text("音色训练轮数",12));settings.Children.Add(sovitsEpochs);settings.Children.Add(Text("语音表达训练轮数",12));settings.Children.Add(gptEpochs);settings.Children.Add(Text("每批处理条数",12));settings.Children.Add(batchBox);advanced.Content=settings;host.Children.Add(advanced);
   trainVersion.SelectionChanged+=(s,e)=>{sovitsEpochs.Text=trainVersion.SelectedIndex==1?"2":"8";};
   host.Children.Add(Row(Button("开始训练声音",StartTraining,true),Button("返回校对",()=>Go(2))));
   if(project.Trained!=null){host.Children.Add(Text("这个项目已有训练完成的声音。",13,"#167B73"));host.Children.Add(Button("使用已训练声音生成配音 →",()=>Go(4)));}
  }
  int Number(TextBox box,int minimum,int maximum){int value;if(!int.TryParse(box.Text,out value)||value<minimum||value>maximum)throw new Exception("训练参数需要是 "+minimum+"～"+maximum+" 之间的整数。");return value;}
  void StartTraining(){if(clips.Count(c=>c.Use)<2 || clips.Any(c=>c.Use&&string.IsNullOrWhiteSpace(c.Text))){Notice("请先保留至少两条录音，并填写所有保留录音对应的文字。");Go(2);return;}try{RunJob("train",new Dictionary<string,object>{{"version",trainVersion.SelectedIndex==1?"v4":"v2Pro"},{"sovits_epochs",Number(sovitsEpochs,1,25)},{"gpt_epochs",Number(gptEpochs,1,50)},{"batch",Number(batchBox,1,8)}});}catch(Exception e){Notice(e.Message);}}
  ComboBox Languages(){ComboBox c=new ComboBox(){Width=125};foreach(string s in new[]{"普通话 / 中英","英语","日语","韩语","粤语"})c.Items.Add(s);c.SelectedIndex=0;return c;}
  string Language(ComboBox c){return new[]{"zh","en","ja","ko","yue"}[Math.Max(0,c.SelectedIndex)];}
  void PageGenerate(){
   if(project.Trained!=null && project.UseTrained)ReferenceDefaults.Apply(project,clips);
   voiceMode=new ComboBox(){Width=350,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,12,0)};voiceMode.Items.Add("快速体验 · 使用参考录音的声音");if(project.Trained!=null)voiceMode.Items.Add("使用这个项目训练完成的声音");voiceMode.SelectedIndex=project.Trained!=null && project.UseTrained?1:0;var voiceActions=new WrapPanel(){Margin=new Thickness(0,0,0,15)};voiceActions.Children.Add(voiceMode);voiceActions.Children.Add(Button("保存音色…",()=>VoiceFileOperation(true)));voiceActions.Children.Add(Button("导入音色…",()=>VoiceFileOperation(false)));host.Children.Add(voiceActions);
   voiceMode.SelectionChanged+=(s,e)=>{project.UseTrained=voiceMode.SelectedIndex==1;if(project.UseTrained && ReferenceDefaults.Apply(project,clips)){referenceBox.Text=project.ReferencePath;referenceText.Text=project.ReferenceText;refLanguage.SelectedIndex=0;}GenerationHelp();SaveProject();};
   Grid columns=new Grid(){Margin=new Thickness(0,0,0,12)};columns.ColumnDefinitions.Add(new ColumnDefinition());columns.ColumnDefinitions.Add(new ColumnDefinition(){Width=new GridLength(24)});columns.ColumnDefinitions.Add(new ColumnDefinition());
   StackPanel left=new StackPanel(),right=new StackPanel();Grid.SetColumn(right,2);columns.Children.Add(left);columns.Children.Add(right);host.Children.Add(columns);
   left.Children.Add(Text("本次配音的示例录音 · 3～10 秒",13,"#233E60"));referenceBox=Input(project.ReferencePath);referenceBox.IsReadOnly=true;referenceBox.ToolTip=project.ReferencePath;left.Children.Add(referenceBox);var refButtons=new WrapPanel(){Margin=new Thickness(0,0,0,8)};refButtons.Children.Add(Button("浏览录音…",PickReference));refButtons.Children.Add(Button("▶ 试听",()=>Play(referenceBox.Text)));if(clips.Count>0)refButtons.Children.Add(Button("从训练片段选…",PickTrainingReference));left.Children.Add(refButtons);
   left.Children.Add(Text("录音里实际说出的文字",13,"#233E60"));referenceText=Input(project.ReferenceText,true,80);left.Children.Add(referenceText);refLanguage=Languages();refLanguage.SelectedIndex=Math.Max(0,Math.Min(4,project.RefLanguage));var refRow=Row(Text("录音语言",12),refLanguage);refRow.Margin=new Thickness(0);left.Children.Add(refRow);
   right.Children.Add(Text("需要生成的新配音文字",13,"#233E60"));targetText=Input(project.TargetText,true,150);right.Children.Add(targetText);textLanguage=Languages();textLanguage.SelectedIndex=Math.Max(0,Math.Min(4,project.TextLanguage));var langRow=Row(Text("配音语言",12),textLanguage);langRow.Margin=new Thickness(0,0,0,8);right.Children.Add(langRow);
   speed=new Slider(){Minimum=0.6,Maximum=1.6,Value=Math.Max(0.6,Math.Min(1.6,project.Speed)),TickFrequency=0.1,IsSnapToTickEnabled=true,Width=170,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,12,0)};TextBlock speedLabel=Text("语速 "+speed.Value.ToString("0.0")+" 倍",12);speed.ValueChanged+=(s,e)=>speedLabel.Text="语速 "+speed.Value.ToString("0.0")+" 倍";var speedRow=Row(speedLabel,speed);speedRow.Margin=new Thickness(0);right.Children.Add(speedRow);
   var actions=Row(Button("生成配音",Generate,true),Button("试听最近生成的配音",()=>Play(project.LastOutput??"")),Button("导出 WAV…",Export));actions.Margin=new Thickness(0,8,0,4);host.Children.Add(actions);
   if(!string.IsNullOrEmpty(project.LastOutput))host.Children.Add(Text("最近保存："+project.LastOutput,11,"#167B73"));
   GenerationHelp();
  }
  void GenerationHelp(){if(voiceMode.SelectedIndex==1){bool fromTraining=clips.Any(c=>string.Equals(c.Path,referenceBox.Text,StringComparison.OrdinalIgnoreCase));subtitle.Text=fromTraining && !string.IsNullOrWhiteSpace(referenceText.Text)?"已从训练片段带入示例录音和文字，直接填写右侧新文稿即可。示例录音用于引导本次语气和节奏，可试听或更换。":"使用已训练的声音；示例录音用于引导本次语气和节奏，可以从训练片段中选择，无需重新录制。";}else subtitle.Text="选择一段示例录音并填写原话，再输入新的文稿。也可以切换到已经训练好的声音。";}
  void PickReference(){OpenFileDialog d=new OpenFileDialog(){Title="选择 3～10 秒的示例录音",Filter="录音文件|*.wav;*.mp3;*.m4a;*.flac;*.ogg;*.aac|所有文件|*.*"};if(d.ShowDialog(window)==true){Clip known=clips.FirstOrDefault(c=>string.Equals(c.Path,d.FileName,StringComparison.OrdinalIgnoreCase));if(!string.Equals(referenceBox.Text,d.FileName,StringComparison.OrdinalIgnoreCase)){referenceText.Text=known==null?"":known.Text??"";}referenceBox.Text=d.FileName;referenceBox.ToolTip=d.FileName;SyncDraft();EnsureProject();SaveProject();GenerationHelp();}}
  void PickTrainingReference(){
   var choices=ReferenceDefaults.Eligible(clips);if(choices.Count==0){Notice("当前没有带文字、时长在 3～10 秒之间的保留片段。可以浏览其他录音，或先在校对页整理一条合适的片段。");return;}
   Window picker=new Window(){Title="选择本次配音的示例录音",Owner=window,Width=720,Height=480,MinWidth=600,MinHeight=380,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=window.FontFamily,FontSize=14,Background=Color("#F3F6FA")};
   Grid layout=new Grid(){Margin=new Thickness(22)};layout.RowDefinitions.Add(new RowDefinition(){Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition());layout.RowDefinitions.Add(new RowDefinition(){Height=GridLength.Auto});picker.Content=layout;
   TextBlock explanation=Text("选择目标人物单独说话、语气适合这次配音的片段。选择后会自动带入录音和文字。",13);layout.Children.Add(explanation);
   ListBox list=new ListBox(){BorderBrush=Color("#CFDAE8"),Margin=new Thickness(0,4,0,16)};Grid.SetRow(list,1);layout.Children.Add(list);
   foreach(Clip clip in choices){StackPanel content=new StackPanel(){Margin=new Thickness(10,7,10,7)};content.Children.Add(Text(clip.Name+"  ·  "+clip.Duration.ToString("0.0")+" 秒",13,"#233E60"));content.Children.Add(Text(clip.Text.Length>76?clip.Text.Substring(0,76)+"…":clip.Text,12));ListBoxItem item=new ListBoxItem(){Content=content,Tag=clip,HorizontalContentAlignment=HorizontalAlignment.Stretch};list.Items.Add(item);if(string.Equals(clip.Path,project.ReferencePath,StringComparison.OrdinalIgnoreCase))list.SelectedItem=item;}
   if(list.SelectedIndex<0)list.SelectedIndex=0;
   Action select=()=>{var item=list.SelectedItem as ListBoxItem;if(item==null)return;Clip clip=(Clip)item.Tag;referenceBox.Text=clip.Path;referenceBox.ToolTip=clip.Path;referenceText.Text=clip.Text;refLanguage.SelectedIndex=0;SyncDraft();SaveProject();GenerationHelp();picker.Close();};
   StackPanel actions=Row(Button("▶ 试听选中片段",()=>{var item=list.SelectedItem as ListBoxItem;if(item!=null)Play(((Clip)item.Tag).Path);}),Button("使用这条录音",select,true));Grid.SetRow(actions,2);layout.Children.Add(actions);list.MouseDoubleClick+=(s,e)=>select();picker.Closed+=(s,e)=>player.Stop();picker.ShowDialog();
  }
  void Generate(){SyncDraft();if(project.UseTrained && ReferenceDefaults.Apply(project,clips)){referenceBox.Text=project.ReferencePath;referenceText.Text=project.ReferenceText;refLanguage.SelectedIndex=0;SaveProject();GenerationHelp();}if(string.IsNullOrWhiteSpace(project.TargetText)){Notice("请在右侧填写需要生成的新配音文字。");targetText.Focus();return;}if(string.IsNullOrWhiteSpace(project.ReferencePath)){Notice("请选择一段 3～10 秒的示例录音，可以点击“从训练片段选”直接复用已有录音。");return;}if(string.IsNullOrWhiteSpace(project.ReferenceText)){Notice("请填写左侧示例录音中实际说出的文字。选择训练片段时会自动带入。");referenceText.Focus();return;}RunJob("synthesize",new Dictionary<string,object>{{"reference",project.ReferencePath},{"reference_text",project.ReferenceText},{"text",project.TargetText},{"ref_lang",Language(refLanguage)},{"text_lang",Language(textLanguage)},{"speed",speed.Value},{"use_trained",voiceMode.SelectedIndex==1},{"trained",project.Trained}});}
  void Export(){if(!File.Exists(project.LastOutput)){Notice("请先生成一段配音。");return;}SaveFileDialog d=new SaveFileDialog(){Title="导出配音",Filter="WAV 音频|*.wav",FileName=Path.GetFileName(project.LastOutput)};if(d.ShowDialog(window)==true){try{if(Path.GetFullPath(d.FileName)!=Path.GetFullPath(project.LastOutput))File.Copy(project.LastOutput,d.FileName,true);status.Text="配音已导出："+d.FileName;}catch(Exception e){Notice("导出失败："+e.Message);}}}
  void CheckReferenceDuration(string path){
   string executable=Path.Combine(engine,"runtime","ffprobe.exe");if(!File.Exists(executable))throw new InvalidDataException("请先在设置中选择完整的语音引擎。");var start=new ProcessStartInfo(executable,"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "+Quote(path)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};using(var probe=Process.Start(start)){string output=probe.StandardOutput.ReadToEnd();string errors=probe.StandardError.ReadToEnd();probe.WaitForExit();double duration;if(probe.ExitCode!=0||!double.TryParse(output.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out duration))throw new InvalidDataException("无法读取示例录音，请换一段能正常播放的录音。");if(duration<3||duration>10)throw new InvalidDataException("保存音色的示例录音需要在 3～10 秒之间，当前为 "+duration.ToString("0.0")+" 秒。");}}
  async void VoiceFileOperation(bool saving){
   if(busy)return;SyncDraft();if(saving && project.UseTrained && ReferenceDefaults.Apply(project,clips)){referenceBox.Text=project.ReferencePath;referenceText.Text=project.ReferenceText;refLanguage.SelectedIndex=0;GenerationHelp();}SaveProject();string path;
   if(saving){if(!File.Exists(project.ReferencePath)||string.IsNullOrWhiteSpace(project.ReferenceText)){Notice("请先选定一段示例录音，并填写对应文字。可以直接从训练片段中选择。");return;}SaveFileDialog dialog=new SaveFileDialog(){Title="保存当前音色（模型、示例录音与文字）",Filter="声线工作室音色文件|*.voicepack",DefaultExt=".voicepack",AddExtension=true,FileName=string.Join("_",project.Name.Split(Path.GetInvalidFileNameChars()))+".voicepack"};if(dialog.ShowDialog(window)!=true)return;path=dialog.FileName;}
   else{OpenFileDialog dialog=new OpenFileDialog(){Title="导入已保存的音色",Filter="声线工作室音色文件|*.voicepack"};if(dialog.ShowDialog(window)!=true)return;path=dialog.FileName;}
   Project snapshot=json.Deserialize<Project>(json.Serialize(project));Project imported=null;string parent=Path.GetDirectoryName(appDir.TrimEnd(Path.DirectorySeparatorChar));if(string.IsNullOrEmpty(parent))parent=appDir;string library=Path.Combine(parent,"VoiceLibrary");
   busy=true;cancelled=false;taskStarted=DateTime.Now;fileCancellation=new CancellationTokenSource();CancellationToken token=fileCancellation.Token;host.IsEnabled=false;cancel.Visibility=Visibility.Visible;progress.Visibility=Visibility.Visible;progress.IsIndeterminate=false;progress.Value=0;Find<TextBlock>("TaskTime").Visibility=Visibility.Visible;Find<TextBlock>("TaskTime").Text="已运行 00:00:00";status.Text=saving?"正在保存音色…":"正在导入音色并校验文件…";int lastPercent=-1;
   Action<double> update=value=>{int rounded=(int)value;if(rounded==lastPercent)return;lastPercent=rounded;window.Dispatcher.BeginInvoke(new Action(()=>{if(!busy||cancelled)return;progress.Value=Math.Min(100,value);status.Text=(saving?"保存音色":"导入音色")+" · "+rounded+"%";}));};
   try{await Task.Run(()=>{if(saving){CheckReferenceDuration(snapshot.ReferencePath);VoicePackage.Export(snapshot,path,token,update);}else imported=VoicePackage.Import(path,library,token,update);});
    if(saving)status.Text="音色已保存："+path;
    else{project=imported;project.TargetText=snapshot.TargetText;clips=new ObservableCollection<Clip>();page=-1;EnsureProject();SaveProject();Settings();Go(4);status.Text="音色已导入，填写新文稿即可生成配音。";}
   }catch(OperationCanceledException){status.Text=saving?"已停止保存音色，原有文件保持完整。":"已停止导入音色。";}
   catch(Exception e){Log(e.ToString());status.Text=saving?"音色保存未完成。":"音色导入未完成。";Notice(e.Message);}
   finally{fileCancellation.Dispose();fileCancellation=null;busy=false;host.IsEnabled=true;cancel.Visibility=Visibility.Collapsed;progress.Visibility=Visibility.Collapsed;Find<TextBlock>("TaskTime").Text="用时 "+(DateTime.Now-taskStarted).ToString(@"hh\:mm\:ss");SaveProject();}
  }
  async void RunJob(string action,Dictionary<string,object> extra){
   if(busy)return;if(!ValidEngine()){Notice("请先点击左下角“设置引擎位置”，选择解压后的 GPT-SoVITS 文件夹。");return;}
   SyncDraft();EnsureProject();SaveProject();
   var request=new Dictionary<string,object>{{"action",action},{"engine",engine},{"project_dir",project.Directory},{"sources",project.Sources},{"clips",clips.ToList()}};if(extra!=null)foreach(var pair in extra)request[pair.Key]=pair.Value;
   string jobs=Path.Combine(project.Directory,"jobs");System.IO.Directory.CreateDirectory(jobs);string id=DateTime.Now.ToString("yyyyMMdd_HHmmss_")+Guid.NewGuid().ToString("N").Substring(0,5);string job=Path.Combine(jobs,id+".json");File.WriteAllText(job,json.Serialize(request),new UTF8Encoding(false));
   string worker;try{worker=EnsureWorker();}catch(Exception e){Notice("无法准备运行文件，请将程序放在可写入的文件夹中："+e.Message);return;}
   busy=true;cancelled=false;jobSucceeded=false;result=null;errorMessage="";taskStarted=DateTime.Now;Find<TextBlock>("TaskTime").Visibility=Visibility.Visible;Find<TextBlock>("TaskTime").Text="已运行 00:00:00";host.IsEnabled=false;cancel.Visibility=Visibility.Visible;progress.Visibility=Visibility.Visible;progress.IsIndeterminate=true;status.Text="正在启动任务…";
   var start=new ProcessStartInfo(Path.Combine(engine,"runtime","python.exe"),"-X utf8 -u -s "+Quote(worker)+" "+Quote(job)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=engine,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
   start.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";start.EnvironmentVariables["PYTHONUTF8"]="1";
   var own=new Process(){StartInfo=start};running=own;object logLock=new object();
   try{
    using(var log=new StreamWriter(Path.Combine(jobs,id+".log"),false,new UTF8Encoding(false))){log.AutoFlush=true;
     DataReceivedEventHandler received=(s,e)=>{if(e.Data==null)return;lock(logLock)log.WriteLine(e.Data);HandleLine(e.Data);};own.OutputDataReceived+=received;own.ErrorDataReceived+=received;
     int code=await Task.Run(()=>{if(cancelled)return -1;own.Start();if(cancelled)KillOwnProcess();own.BeginOutputReadLine();own.BeginErrorReadLine();own.WaitForExit();return own.ExitCode;});
     if(cancelled){status.Text="任务已停止。原始素材和已保存的项目仍然保留。";}
     else if(code!=0 || result==null){status.Text="任务未完成。可以展开详细日志查看原因。";Notice(string.IsNullOrWhiteSpace(errorMessage)?"任务未完成，请查看下方详细日志。":errorMessage);}
     else{ApplyResult(action,result);jobSucceeded=true;status.Text=action=="prepare"?"录音已整理完成，可以试听和筛选。":action=="transcribe"?"识别完成，请试听并校对文字。":action=="train"?"声音训练完成，可以生成配音了。":"配音已生成，并保存在项目的 exports 文件夹。";}
    }
   }catch(Exception e){status.Text="任务启动失败。";Notice(e.Message);}finally{busy=false;running=null;own.Dispose();host.IsEnabled=true;cancel.Visibility=Visibility.Collapsed;progress.Visibility=Visibility.Collapsed;Find<TextBlock>("TaskTime").Text="用时 "+(DateTime.Now-taskStarted).ToString(@"hh\:mm\:ss");SaveProject();}
  }
  static string Quote(string value){return "\""+value.Replace("\"","\\\"")+"\"";}
  string EnsureWorker(){string directory=Path.Combine(appDir,"internal");System.IO.Directory.CreateDirectory(directory);string path=Path.Combine(directory,"worker.py");string source;using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("worker.py"))using(var reader=new StreamReader(stream,Encoding.UTF8))source=reader.ReadToEnd();if(!File.Exists(path)||File.ReadAllText(path,Encoding.UTF8)!=source)AtomicWrite(path,source);return path;}
  void HandleLine(string line){
   if(window.Dispatcher.HasShutdownStarted)return;
   if(!line.StartsWith("@VOICE_STUDIO:")){window.Dispatcher.BeginInvoke(new Action(()=>Log(line)));return;}
   try{var data=json.Deserialize<Dictionary<string,object>>(line.Substring("@VOICE_STUDIO:".Length));string kind=Convert.ToString(data["kind"]);
    if(kind=="result"){result=data;return;}if(kind=="error")errorMessage=Convert.ToString(data["message"]);
    window.Dispatcher.BeginInvoke(new Action(()=>{if(cancelled || !busy)return;if(kind=="progress"){status.Text=Convert.ToString(data["message"]);double percent=Convert.ToDouble(data["percent"],CultureInfo.InvariantCulture);progress.IsIndeterminate=percent<0;if(percent>=0)progress.Value=Math.Min(100,percent);}else if(kind=="clip"){string path=Convert.ToString(data["path"]);Clip clip=clips.FirstOrDefault(c=>c.Path==path);if(clip!=null)clip.Text=Convert.ToString(data["text"]);}else if(data.ContainsKey("message"))Log(Convert.ToString(data["message"]));}));
   }catch(Exception e){window.Dispatcher.BeginInvoke(new Action(()=>Log("状态读取失败："+e.Message)));}
  }
  void ApplyResult(string action,Dictionary<string,object> data){if(action=="prepare" || action=="transcribe"){var loaded=json.Deserialize<List<Clip>>(json.Serialize(data["clips"]));clips=new ObservableCollection<Clip>(loaded);Go(action=="prepare"?1:2);}else if(action=="train"){project.Trained=json.Deserialize<Dictionary<string,object>>(json.Serialize(data["trained"]));project.UseTrained=true;Go(4);}else if(action=="synthesize"){project.LastOutput=Convert.ToString(data["output"]);Go(4);}SaveProject();}
  void KillOwnProcess(){try{Process p=running;if(p==null || p.HasExited)return;ProcessStartInfo si=new ProcessStartInfo("taskkill.exe","/PID "+p.Id+" /T /F"){UseShellExecute=false,CreateNoWindow=true};using(Process killer=Process.Start(si))killer.WaitForExit(5000);}catch(Exception){}}
  void OpenFolder(string directory){if(!System.IO.Directory.Exists(directory))return;Process.Start(new ProcessStartInfo("explorer.exe",Quote(directory)){UseShellExecute=true});}
  public void Show(){window.Show();}
  void NativeTest(string projectFile,string report){
   LoadProject(projectFile);Go(0);window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-5000;window.Top=-5000;window.ShowInTaskbar=false;window.Show();
   bool started=false;window.Dispatcher.BeginInvoke(new Action(()=>{started=true;RunJob("prepare",null);}));var timer=new DispatcherTimer(){Interval=TimeSpan.FromMilliseconds(200)};timer.Tick+=(s,e)=>{if(!started || busy)return;timer.Stop();bool success=jobSucceeded && clips.Count>0;File.WriteAllText(report,json.Serialize(new{success=success,clip_count=clips.Count,status=status.Text,error=errorMessage,finished=DateTime.Now.ToString("o")}),new UTF8Encoding(false));if(!success)Environment.ExitCode=1;window.Close();};timer.Start();
  }
  public void Screenshots(string folder){
   System.IO.Directory.CreateDirectory(folder);window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-5000;window.Top=-5000;window.ShowInTaskbar=false;window.Show();window.UpdateLayout();
   for(int i=0;i<5;i++){Go(i);window.UpdateLayout();FrameworkElement visual=(FrameworkElement)window.Content;var target=new RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);target.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(target));using(var stream=File.Create(Path.Combine(folder,"screen_"+(i+1)+".png")))encoder.Save(stream);}
   window.Close();
  }
  static void TestReferenceDefaults(string report,string actualProject){
   var serializer=new JavaScriptSerializer(){MaxJsonLength=32*1024*1024};string dir=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)),"reference_fixtures");System.IO.Directory.CreateDirectory(dir);
   string a=Path.Combine(dir,"a.wav"),b=Path.Combine(dir,"b.wav"),external=Path.Combine(dir,"external.wav");foreach(string p in new[]{a,b,external})File.WriteAllBytes(p,new byte[]{0});
   var source=new List<Clip>{new Clip(){Use=true,Name="a.wav",Path=a,Duration=4,Text="第一段已校对文字。"},new Clip(){Use=true,Name="b.wav",Path=b,Duration=6,Text="第二段已校对文字。"},new Clip(){Use=false,Name="excluded",Path=a,Duration=6,Text="不保留。"},new Clip(){Use=true,Name="too_short",Path=a,Duration=2,Text="太短。"},new Clip(){Use=true,Name="blank_text",Path=a,Duration=6,Text=""},new Clip(){Use=true,Name="missing",Path=Path.Combine(dir,"missing.wav"),Duration=6,Text="文件缺失。"}};
   var checks=new Dictionary<string,bool>();var p1=new Project(){TargetText="保留新文稿",Trained=new Dictionary<string,object>{{"version","v4"}}};checks["eligible_filters"]=ReferenceDefaults.Eligible(source).Count==2;
   checks["autofill_recording_and_transcript"]=ReferenceDefaults.Apply(p1,source)&&p1.ReferencePath==b&&p1.ReferenceText==source[1].Text&&p1.RefLanguage==0;
   checks["preserve_model_and_target"]=Convert.ToString(p1.Trained["version"])=="v4"&&p1.TargetText=="保留新文稿";
   var p2=new Project(){ReferencePath=external,ReferenceText="用户选择的示例。",RefLanguage=1};checks["preserve_external_choice"]=!ReferenceDefaults.Apply(p2,source)&&p2.ReferencePath==external&&p2.ReferenceText=="用户选择的示例。"&&p2.RefLanguage==1;
   var p3=new Project(){ReferencePath=a,ReferenceText=""};checks["fill_existing_clip_transcript"]=ReferenceDefaults.Apply(p3,source)&&p3.ReferencePath==a&&p3.ReferenceText==source[0].Text;
   var p4=new Project();checks["no_candidate_does_not_fabricate_reference"]=!ReferenceDefaults.Apply(p4,new List<Clip>{source[2],source[3],source[4],source[5]})&&string.IsNullOrEmpty(p4.ReferencePath);
   var p5=new Project(){ReferencePath=b,ReferenceText="用户校对过的内容"};checks["preserve_existing_transcript_edit"]=!ReferenceDefaults.Apply(p5,source)&&p5.ReferenceText=="用户校对过的内容";
   object userResult=null;if(!string.IsNullOrEmpty(actualProject)){Project current=serializer.Deserialize<Project>(File.ReadAllText(actualProject,Encoding.UTF8));string models=serializer.Serialize(current.Trained);int count=ReferenceDefaults.Eligible(current.Clips).Count;bool changed=ReferenceDefaults.Apply(current,current.Clips);checks["actual_project_reference_is_usable"]=File.Exists(current.ReferencePath)&&!string.IsNullOrWhiteSpace(current.ReferenceText)&&models==serializer.Serialize(current.Trained);userResult=new{candidate_count=count,autofilled=changed,selected_filename=Path.GetFileName(current.ReferencePath)};}
   bool success=checks.Values.All(v=>v);File.WriteAllText(report,serializer.Serialize(new{success=success,checks=checks,actual_project=userResult}),new UTF8Encoding(false));Environment.ExitCode=success?0:1;
  }
  static void TestVoicePackage(string projectFile,string report){
   var serializer=new JavaScriptSerializer(){MaxJsonLength=32*1024*1024};var checks=new Dictionary<string,bool>();Project source=serializer.Deserialize<Project>(File.ReadAllText(projectFile,Encoding.UTF8));ReferenceDefaults.Apply(source,source.Clips);source.UseTrained=true;source.TargetText="保留文稿";string dir=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)),"voicepack_roundtrip");System.IO.Directory.CreateDirectory(dir);string package=Path.Combine(dir,"测试音色.voicepack");VoicePackage.Export(source,package,CancellationToken.None,null);Project imported=VoicePackage.Import(package,Path.Combine(dir,"library"),CancellationToken.None,null);
   checks["trained_mode_preserved"]=imported.UseTrained&&Convert.ToString(imported.Trained["version"])==Convert.ToString(source.Trained["version"]);
   checks["reference_and_language_preserved"]=imported.ReferenceText==source.ReferenceText&&imported.RefLanguage==source.RefLanguage&&File.Exists(imported.ReferencePath);
   checks["models_relocated"]=File.Exists(Convert.ToString(imported.Trained["sovits"]))&&File.Exists(Convert.ToString(imported.Trained["gpt"]))&&Convert.ToString(imported.Trained["sovits"]).StartsWith(dir,StringComparison.OrdinalIgnoreCase);
   checks["source_project_not_changed"]=source.TargetText=="保留文稿"&&File.Exists(Convert.ToString(source.Trained["sovits"]));
   string canceled=Path.Combine(dir,"canceled.voicepack");using(var stop=new CancellationTokenSource()){stop.Cancel();try{VoicePackage.Export(source,canceled,stop.Token,null);checks["canceled_export_leaves_no_file"]=false;}catch(OperationCanceledException){checks["canceled_export_leaves_no_file"]=!File.Exists(canceled)&&!System.IO.Directory.GetFiles(dir,"canceled.voicepack.partial-*").Any();}}
   string invalid=Path.Combine(dir,"unsafe.voicepack");using(var stream=File.Create(invalid))using(var zip=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create)){var entry=zip.CreateEntry("voice.json");using(var writer=new StreamWriter(entry.Open()))writer.Write(serializer.Serialize(new VoiceManifest(){FormatVersion=1,Mode="reference",Name="invalid",ReferenceFile="../escape.wav",ReferenceText="文字",Files=new List<VoiceFile>()}));}try{VoicePackage.Import(invalid,Path.Combine(dir,"unsafe_library"),CancellationToken.None,null);checks["unsafe_path_rejected"]=false;}catch(InvalidDataException){checks["unsafe_path_rejected"]=!File.Exists(Path.Combine(dir,"escape.wav"));}
   string corrupt=Path.Combine(dir,"corrupt.voicepack");using(var stream=File.Create(corrupt))using(var zip=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create)){var entry=zip.CreateEntry("voice.json");using(var writer=new StreamWriter(entry.Open()))writer.Write(serializer.Serialize(new VoiceManifest(){FormatVersion=1,Mode="reference",Name="corrupt",ReferenceFile="reference.wav",ReferenceText="文字",Files=new List<VoiceFile>{new VoiceFile(){Path="reference.wav",Bytes=3,Sha256=new string('0',64)}}}));using(var audio=zip.CreateEntry("reference.wav").Open())audio.Write(new byte[]{1,2,3},0,3);}string corruptLibrary=Path.Combine(dir,"corrupt_library");try{VoicePackage.Import(corrupt,corruptLibrary,CancellationToken.None,null);checks["corrupt_file_rejected_and_cleaned"]=false;}catch(InvalidDataException){checks["corrupt_file_rejected_and_cleaned"]=!System.IO.Directory.Exists(corruptLibrary)||System.IO.Directory.GetDirectories(corruptLibrary).Length==0;}
   bool success=checks.Values.All(v=>v);File.WriteAllText(report,serializer.Serialize(new{success=success,checks=checks,package_bytes=new FileInfo(package).Length,package=package}),new UTF8Encoding(false));Environment.ExitCode=success?0:1;
  }
  [STAThread] public static void Main(string[] args){
   if(args.Length>2&&args[0]=="--voicepack-test"){try{TestVoicePackage(args[1],args[2]);}catch(Exception e){File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(new{success=false,error=e.ToString()}));Environment.ExitCode=1;}return;}
   if(args.Length>1&&args[0]=="--reference-test"){TestReferenceDefaults(args[1],args.Length>2?args[2]:null);return;}
   try{var app=new Application(){ShutdownMode=ShutdownMode.OnMainWindowClose};bool smoke=args.Length>0 && (args[0]=="--screenshots"||args[0]=="--native-test");Studio studio=new Studio(smoke);app.MainWindow=studio.window;
    if(args.Length>0 && args[0]=="--screenshots"){studio.Screenshots(args.Length>1?args[1]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"screenshots"));return;}
    if(args.Length>0 && args[0]=="--native-test"){studio.NativeTest(args[1],args[2]);app.Run();return;}
    studio.Show();app.Run();
   }catch(Exception e){if(args.Length>0 && args[0]=="--screenshots"){System.IO.Directory.CreateDirectory(args[1]);File.WriteAllText(Path.Combine(args[1],"startup-error.txt"),e.ToString());Environment.ExitCode=1;}else if(args.Length>2 && args[0]=="--native-test"){File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(new{success=false,error=e.ToString()}));Environment.ExitCode=1;}else MessageBox.Show(e.ToString(),"声线工作室启动失败");}
  }
 }
}
