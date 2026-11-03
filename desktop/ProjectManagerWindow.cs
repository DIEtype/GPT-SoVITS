using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceStudio {
 public class ProjectManagerWindow {
  readonly Project project;readonly Window owner;
  public readonly Window Window;
  FileCatalog catalog;ComboBox runs,sovits,gpt,filter;DataGrid files;TabControl tabs;TextBlock total,selection,detail,status,runInfo;Button apply,recycle;ICollectionView view;bool cleaning,testMode;
  public Dictionary<string,object> SelectedModel {get;private set;}
  static Brush Ink(string color){return (Brush)new BrushConverter().ConvertFromString(color);}
  TextBlock Text(string text,int size=13){return new TextBlock(){Text=text,FontSize=size,Foreground=Ink("#526880"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};}
  Button Button(string text,Action action,bool primary=false){var button=new Button(){Content=text,FontSize=13};if(primary)button.Style=(Style)owner.Resources["Primary"];button.Click+=(s,e)=>action();return button;}
  StackPanel Row(params UIElement[] elements){var row=new StackPanel(){Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};foreach(var element in elements)row.Children.Add(element);return row;}
  public ProjectManagerWindow(Window owner,Project project){
   this.owner=owner;this.project=project;catalog=ProjectFiles.Scan(project);
   Window=new Window(){Owner=owner,Title="项目文件管理",Width=1080,Height=790,MinWidth=900,MinHeight=650,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=owner.FontFamily,FontSize=14,Background=Ink("#F3F6FA")};Window.Resources.MergedDictionaries.Add(owner.Resources);
   var background=new Grid(){Background=Ink("#F3F6FA")};var shell=new Grid(){Margin=new Thickness(24)};shell.RowDefinitions.Add(new RowDefinition(){Height=GridLength.Auto});shell.RowDefinitions.Add(new RowDefinition());shell.RowDefinitions.Add(new RowDefinition(){Height=GridLength.Auto});background.Children.Add(shell);Window.Content=background;
   var header=new Grid(){Margin=new Thickness(0,0,0,16)};header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition(){Width=GridLength.Auto});shell.Children.Add(header);
   var intro=new StackPanel();intro.Children.Add(new TextBlock(){Text="项目文件管理",FontSize=25,FontWeight=FontWeights.Bold,Foreground=Ink("#172A43"),Margin=new Thickness(0,0,0,6)});intro.Children.Add(Text(project.Name+" · "+ProjectFiles.Describe(project.Trained),12));var path=Text(catalog.Root,11);path.TextWrapping=TextWrapping.NoWrap;path.TextTrimming=TextTrimming.CharacterEllipsis;path.ToolTip=catalog.Root;intro.Children.Add(path);header.Children.Add(intro);
   var usage=new StackPanel(){HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(20,0,0,0)};total=Text("",25);total.Foreground=Ink("#167B73");usage.Children.Add(total);usage.Children.Add(Text("项目文件逻辑大小",11));Grid.SetColumn(usage,1);header.Children.Add(usage);
   tabs=new TabControl(){Background=Ink("#FFFFFF"),BorderBrush=Ink("#DDE5EF"),Padding=new Thickness(18)};Grid.SetRow(tabs,1);shell.Children.Add(tabs);
   tabs.Items.Add(new TabItem(){Header="训练版本",Content=TrainingPanel(),Padding=new Thickness(18,8,18,8)});tabs.Items.Add(new TabItem(){Header="文件清理",Content=FilesPanel(),Padding=new Thickness(18,8,18,8)});
   status=Text("选择训练版本去配音，或按文件用途清理项目。",12);status.Margin=new Thickness(0,14,0,0);Grid.SetRow(status,2);shell.Children.Add(status);Refresh();
   Window.Closing+=(s,e)=>{if(cleaning){e.Cancel=true;status.Text="文件操作进行中，请等待完成或在系统进度窗口中取消。";}};
  }
  UIElement TrainingPanel(){
   var panel=new StackPanel();panel.Children.Add(Text("两阶段训练分别保留自己的 Epoch。先固定其中一个，再切换另一个比较效果；参考录音、文稿和语速会继续沿用。"));
   panel.Children.Add(Text("训练记录",12));runs=new ComboBox(){DisplayMemberPath="Label",Margin=new Thickness(0,0,0,18)};runs.SelectionChanged+=(s,e)=>LoadEpochs();panel.Children.Add(runs);
   var choices=new Grid(){Margin=new Thickness(0,0,0,14)};choices.ColumnDefinitions.Add(new ColumnDefinition());choices.ColumnDefinitions.Add(new ColumnDefinition());StackPanel left=new StackPanel(){Margin=new Thickness(0,0,12,0)},right=new StackPanel();Grid.SetColumn(right,1);choices.Children.Add(left);choices.Children.Add(right);panel.Children.Add(choices);
   left.Children.Add(Text("音色模型 · SoVITS",13));right.Children.Add(Text("语音表达 · GPT",13));sovits=new ComboBox(){DisplayMemberPath="Label"};gpt=new ComboBox(){DisplayMemberPath="Label"};left.Children.Add(sovits);right.Children.Add(gpt);
   runInfo=Text("",12);panel.Children.Add(runInfo);apply=Button("使用所选版本去配音",SelectModel,true);panel.Children.Add(Row(apply,Button("打开项目文件夹",()=>OpenFolder(catalog.Root))));
   panel.Children.Add(Text("较早 Epoch 不一定更差，较晚 Epoch 也不保证最好。这里选择的是一次训练的参数快照，不会重新训练或合并模型。",12));return panel;
  }
  UIElement FilesPanel(){
   var panel=new Grid();panel.RowDefinitions.Add(new RowDefinition(){Height=GridLength.Auto});panel.RowDefinitions.Add(new RowDefinition());panel.RowDefinitions.Add(new RowDefinition(){Height=GridLength.Auto});
   var tools=new StackPanel();filter=new ComboBox(){Width=145};foreach(string kind in new[]{"全部文件",ProjectFiles.Weights,ProjectFiles.Checkpoints,ProjectFiles.Features,ProjectFiles.Audio,ProjectFiles.Exports,ProjectFiles.Logs,ProjectFiles.Records})filter.Items.Add(kind);filter.SelectedIndex=0;filter.SelectionChanged+=(s,e)=>{if(view!=null)view.Refresh();};
   var actions=new WrapPanel(){Margin=new Thickness(0,0,0,10)};actions.Children.Add(filter);actions.Children.Add(Button("勾选中间轮次",()=>SelectKind(ProjectFiles.Weights,true)));actions.Children.Add(Button("勾选当前分类",()=>{foreach(ManagedFile file in view)file.Selected=file.CanRecycle&&!file.Latest;UpdateSelection();}));actions.Children.Add(Button("清空勾选",()=>{foreach(var file in catalog.Files)file.Selected=false;}));actions.Children.Add(Button("刷新",Refresh));tools.Children.Add(actions);
   tools.Children.Add(Text("当前项目及同一 Projects 目录下其他项目引用的文件受保护。批量勾选会保留每次训练的最新版本；选中一行可查看完整路径。",12));panel.Children.Add(tools);
   files=new DataGrid(){AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,RowHeight=42,FontSize=12,Margin=new Thickness(0,0,0,10)};Grid.SetRow(files,1);panel.Children.Add(files);
   var box=new FrameworkElementFactory(typeof(CheckBox));box.SetBinding(CheckBox.IsCheckedProperty,new Binding("Selected"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});box.SetBinding(CheckBox.IsEnabledProperty,new Binding("CanRecycle"));box.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);box.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);files.Columns.Add(new DataGridTemplateColumn(){Header="选择",CellTemplate=new DataTemplate(){VisualTree=box},Width=52});
   files.Columns.Add(new DataGridTextColumn(){Header="类型",Binding=new Binding("KindLabel"),IsReadOnly=true,Width=105});files.Columns.Add(new DataGridTextColumn(){Header="Epoch",Binding=new Binding("EpochText"),SortMemberPath="EpochNumber",IsReadOnly=true,Width=70});files.Columns.Add(new DataGridTextColumn(){Header="文件名",Binding=new Binding("FileName"),IsReadOnly=true,Width=new DataGridLength(1,DataGridLengthUnitType.Star)});files.Columns.Add(new DataGridTextColumn(){Header="大小",Binding=new Binding("SizeText"),IsReadOnly=true,Width=100});files.Columns.Add(new DataGridTextColumn(){Header="状态",Binding=new Binding("State"),IsReadOnly=true,Width=155});
   files.SizeChanged+=(s,e)=>{files.Columns[3].Width=new DataGridLength(Math.Max(260,files.ActualWidth-52-105-70-100-155-28));};
   files.SelectionChanged+=(s,e)=>{var file=files.SelectedItem as ManagedFile;detail.Text=file==null?"":file.Path;};
   var footer=new StackPanel();Grid.SetRow(footer,2);panel.Children.Add(footer);selection=Text("",13);footer.Children.Add(selection);detail=Text("",11);detail.MaxHeight=38;footer.Children.Add(detail);
   recycle=Button("将勾选文件移入回收站",()=>RecycleSelected(),true);footer.Children.Add(Row(recycle,Button("定位选中文件",()=>{var file=files.SelectedItem as ManagedFile;if(file==null){status.Text="请先点击一行文件。";return;}Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+file.Path+"\""){UseShellExecute=true});}),Button("打开系统回收站",()=>Process.Start(new ProcessStartInfo("explorer.exe","shell:RecycleBinFolder"){UseShellExecute=true}))));
   footer.Children.Add(Text("清空系统回收站后才释放空间；恢复检查点删除后不能从该断点续训，特征缓存清理后需重新计算。硬链接可能使实际释放空间小于文件大小。",11));return panel;
  }
  void Refresh(){
   if(cleaning)return;try{catalog=ProjectFiles.Scan(project);total.Text=ProjectFiles.Size(catalog.Bytes);foreach(var file in catalog.Files)file.PropertyChanged+=(s,e)=>UpdateSelection();view=CollectionViewSource.GetDefaultView(catalog.Files);view.Filter=item=>filter.SelectedIndex==0||((ManagedFile)item).Kind==Convert.ToString(filter.SelectedItem);files.ItemsSource=view;
    runs.ItemsSource=catalog.Runs;string current=project.Trained!=null&&project.Trained.ContainsKey("run")?Convert.ToString(project.Trained["run"]):"";runs.SelectedItem=catalog.Runs.FirstOrDefault(r=>string.Equals(r.Path,current,StringComparison.OrdinalIgnoreCase))??catalog.Runs.FirstOrDefault();LoadEpochs();UpdateSelection();
   }catch(Exception error){if(status!=null)status.Text=error.Message;else throw;}
  }
  void LoadEpochs(){
   if(sovits==null||gpt==null||apply==null)return;var run=runs.SelectedItem as TrainingRun;sovits.ItemsSource=run==null?null:run.SoVITS;gpt.ItemsSource=run==null?null:run.GPT;
   if(run==null){apply.IsEnabled=false;runInfo.Text="这个项目还没有可选的训练记录。导入音色的权重位于外部 VoiceLibrary，不在本项目内清理。";return;}
   sovits.SelectedItem=run.SoVITS.FirstOrDefault(f=>f.Current)??run.SoVITS.LastOrDefault();gpt.SelectedItem=run.GPT.FirstOrDefault(f=>f.Current)??run.GPT.LastOrDefault();apply.IsEnabled=run.SoVITS.Count>0&&run.GPT.Count>0;runInfo.Text=ModelSelection.Name(run.Version)+" · 音色 "+run.SoVITS.Count+" 个版本，语音表达 "+run.GPT.Count+" 个版本";
  }
  void SelectModel(){try{var run=runs.SelectedItem as TrainingRun;var voice=sovits.SelectedItem as EpochFile;var speech=gpt.SelectedItem as EpochFile;if(run==null||voice==null||speech==null)return;SelectedModel=ProjectFiles.Select(project,run.Path,voice.Path,speech.Path);if(!testMode)Window.DialogResult=true;}catch(Exception error){status.Text=error.Message;}}
  void SelectKind(string kind,bool middle){filter.SelectedItem=kind;foreach(var file in catalog.Files)file.Selected=file.Kind==kind&&file.CanRecycle&&(!middle||(!file.Latest&&ProjectFiles.Epoch(file.Path)>0));UpdateSelection();}
  void UpdateSelection(){if(selection==null)return;var selected=catalog.Files.Where(f=>f.Selected).ToList();selection.Text="已勾选 "+selected.Count+" 个文件 · "+ProjectFiles.Size(selected.Sum(f=>f.Bytes))+(selected.Any(f=>f.Kind==ProjectFiles.Checkpoints)?" · 含恢复训练的断点":"");recycle.IsEnabled=!cleaning&&selected.Count>0;}
  void OpenFolder(string path){Process.Start(new ProcessStartInfo("explorer.exe","\""+path+"\""){UseShellExecute=true});}
  async void RecycleSelected(){
   if(cleaning)return;var chosen=catalog.Files.Where(f=>f.Selected).ToList();if(chosen.Count==0)return;
   string message="将 "+chosen.Count+" 个文件（"+ProjectFiles.Size(chosen.Sum(f=>f.Bytes))+"）移入系统回收站？\n\n"+(chosen.Any(f=>f.Kind==ProjectFiles.Checkpoints)?"包含恢复检查点；删除后无法从这些断点继续训练。\n":"")+"当前使用的模型和录音受保护。清空回收站后才会释放磁盘空间。";
   if(MessageBox.Show(Window,message,"确认清理选中的项目文件",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
   cleaning=true;tabs.IsEnabled=false;status.Text="正在核对引用并移入回收站…";IntPtr handle=new WindowInteropHelper(Window).Handle;var completion=new TaskCompletionSource<CleanupResult>();
   var thread=new Thread(()=>{try{completion.SetResult(ProjectFiles.Recycle(project,chosen,paths=>WindowsRecycle.Send(paths,handle)));}catch(Exception error){completion.SetException(error);}});thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
   try{var result=await completion.Task;status.Text="已移入回收站 "+result.Removed+" 个文件 · "+ProjectFiles.Size(result.Bytes)+(string.IsNullOrEmpty(result.Error)?"。可从系统回收站恢复。":"；部分文件未处理："+result.Error);}catch(Exception error){status.Text="清理未完成："+error.Message;}finally{cleaning=false;tabs.IsEnabled=true;Refresh();}
  }
  void Capture(string path){Window.UpdateLayout();var visual=(FrameworkElement)Window.Content;var bitmap=new RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);}
  public void RenderForTest(string folder){
   testMode=true;Directory.CreateDirectory(folder);Window.WindowStartupLocation=WindowStartupLocation.Manual;Window.Left=-5000;Window.Top=-5000;Window.ShowInTaskbar=false;Window.Show();var checks=new Dictionary<string,bool>();
   var run=runs.SelectedItem as TrainingRun;if(run!=null&&run.GPT.Count>0)gpt.SelectedIndex=0;SelectModel();checks["epoch_selection_applies"]=SelectedModel!=null&&ProjectFiles.Epoch(Convert.ToString(SelectedModel["gpt"]))==(run==null||run.GPT.Count==0?0:run.GPT[0].Epoch);Capture(System.IO.Path.Combine(folder,"training_versions.png"));
   tabs.SelectedIndex=1;SelectKind(ProjectFiles.Weights,true);checks["bulk_selection_excludes_current_and_latest"]=catalog.Files.Where(f=>f.Selected).All(f=>f.CanRecycle&&!f.Latest);Capture(System.IO.Path.Combine(folder,"project_files.png"));
   File.WriteAllText(System.IO.Path.Combine(folder,"report.json"),new JavaScriptSerializer().Serialize(new{success=checks.Values.All(v=>v),checks=checks,selected=catalog.Files.Count(f=>f.Selected),files=catalog.Files.Count}),new UTF8Encoding(false));Environment.ExitCode=checks.Values.All(v=>v)?0:1;Window.Close();
  }
 }
}
