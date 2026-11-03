using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VoiceStudio {
 public class ManagedFile : INotifyPropertyChanged {
  bool selected;
  public string Path {get;set;} public string Relative {get;set;} public string Kind {get;set;} public string Reason {get;set;}
  public long Bytes {get;set;} public long ModifiedTicks {get;set;} public bool CanRecycle {get;set;} public bool Latest {get;set;}
  public string SizeText {get{return ProjectFiles.Size(Bytes);}}
  public string FileName {get{return System.IO.Path.GetFileName(Path);}}
  public string KindLabel {get{return Kind==ProjectFiles.Weights?(Relative.Replace('\\','/').Contains("/models/SoVITS/")?"音色权重":"语音表达权重"):Kind;}}
  public int EpochNumber {get{return Kind==ProjectFiles.Weights?ProjectFiles.Epoch(Path):0;}}
  public string EpochText {get{return EpochNumber>0?EpochNumber.ToString():"—";}}
  public string State {get{return !CanRecycle?Reason:Latest?"最新保留版本":"可清理";}}
  public bool Selected {get{return selected;}set{bool next=value&&CanRecycle;if(next==selected)return;selected=next;if(PropertyChanged!=null)PropertyChanged(this,new PropertyChangedEventArgs("Selected"));}}
  public event PropertyChangedEventHandler PropertyChanged;
 }
 public class EpochFile {
  public string Path {get;set;} public int Epoch {get;set;} public long Bytes {get;set;} public bool Current {get;set;}
  public string Label {get{return "Epoch "+Epoch+" · "+ProjectFiles.Size(Bytes)+(Current?" · 当前使用":"");}}
 }
 public class TrainingRun {
  public string Path {get;set;} public string Version {get;set;} public string Label {get;set;}
  public List<EpochFile> SoVITS=new List<EpochFile>(),GPT=new List<EpochFile>();
 }
 public class FileCatalog {
  public string Root {get;set;} public List<ManagedFile> Files=new List<ManagedFile>();public List<TrainingRun> Runs=new List<TrainingRun>();
  public long Bytes {get{return Files.Sum(f=>f.Bytes);}}
 }
 public class CleanupResult {public int Removed {get;set;}public long Bytes {get;set;}public string Error {get;set;}}
 public static class ProjectFiles {
  public const string Weights="训练权重",Checkpoints="训练恢复点",Features="训练特征",Audio="音频缓存",Exports="生成配音",Logs="运行日志",Records="项目记录";
  static JavaScriptSerializer Json(){return new JavaScriptSerializer(){MaxJsonLength=32*1024*1024};}
  public static string Full(string path){string full=System.IO.Path.GetFullPath(path);return full.Length>System.IO.Path.GetPathRoot(full).Length?full.TrimEnd(System.IO.Path.DirectorySeparatorChar,System.IO.Path.AltDirectorySeparatorChar):full;}
  public static bool Inside(string root,string path){return Full(path).StartsWith(Full(root)+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
  public static string Size(long bytes){return bytes>=1024L*1024*1024?(bytes/(1024.0*1024*1024)).ToString("0.00")+" GiB":bytes>=1024*1024?(bytes/(1024.0*1024)).ToString("0.0")+" MiB":bytes>=1024?(bytes/1024.0).ToString("0.0")+" KiB":bytes+" B";}
  public static int Epoch(string path){var match=Regex.Match(System.IO.Path.GetFileName(path),@"(?:^|[_-])e(\d+)(?=[_.-]|$)",RegexOptions.IgnoreCase);int value;return match.Success&&int.TryParse(match.Groups[1].Value,out value)?value:0;}
  public static string Describe(Dictionary<string,object> trained){if(trained==null)return "尚未选择训练模型";int voice=Epoch(Convert.ToString(trained["sovits"])),speech=Epoch(Convert.ToString(trained["gpt"]));string name=ModelSelection.Name(Convert.ToString(trained["version"]));return voice==0&&speech==0?name+" · 导入模型（未记录 Epoch）":name+" · SoVITS Epoch "+(voice==0?"未记录":voice.ToString())+" / GPT Epoch "+(speech==0?"未记录":speech.ToString());}
  static void SafePath(string root,string path){
   if(!Inside(root,path))throw new InvalidDataException("文件位于项目目录之外，不能在这里清理。");
   string current=Full(path),boundary=Full(root);
   while(!string.IsNullOrEmpty(current)){
    if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("不能清理符号链接或目录联接中的文件。");
    if(string.Equals(current,boundary,StringComparison.OrdinalIgnoreCase))return;
    current=System.IO.Path.GetDirectoryName(current);
   }
   throw new InvalidDataException("项目路径无法验证。");
  }
  static List<string> Walk(string root){
   var result=new List<string>();var pending=new Stack<string>();pending.Push(root);
   while(pending.Count>0){string directory=pending.Pop();foreach(string file in Directory.GetFiles(directory)){SafePath(root,file);result.Add(file);}foreach(string child in Directory.GetDirectories(directory)){if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)pending.Push(child);}}
   return result;
  }
  static void Reference(Dictionary<string,string> protectedPaths,string path,string why){if(!string.IsNullOrWhiteSpace(path))protectedPaths[Full(path)]=why;}
  static void AddReferences(Dictionary<string,string> paths,Project project,string why){
   foreach(string path in project.Sources??new List<string>())Reference(paths,path,why);
   foreach(Clip clip in project.Clips??new List<Clip>())Reference(paths,clip.Path,why);
   Reference(paths,project.ReferencePath,why);Reference(paths,project.LastOutput,why);
   if(project.Trained!=null){object value;if(project.Trained.TryGetValue("sovits",out value))Reference(paths,Convert.ToString(value),why);if(project.Trained.TryGetValue("gpt",out value))Reference(paths,Convert.ToString(value),why);}
  }
  static Dictionary<string,string> Protected(Project current){
   var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);AddReferences(result,current,"当前项目使用中");
   string parent=System.IO.Path.GetDirectoryName(Full(current.Directory));
   foreach(string sibling in Directory.GetDirectories(parent)){
    if((File.GetAttributes(sibling)&FileAttributes.ReparsePoint)!=0)continue;
    string file=System.IO.Path.Combine(sibling,"project.json");if(!File.Exists(file))continue;
    Project saved;try{saved=Json().Deserialize<Project>(File.ReadAllText(file,Encoding.UTF8));if(saved==null)throw new InvalidDataException();}catch(Exception error){throw new InvalidDataException("项目记录无法读取，暂不能确认文件引用："+file,error);}
    AddReferences(result,saved,string.Equals(Full(sibling),Full(current.Directory),StringComparison.OrdinalIgnoreCase)?"当前项目使用中":"其他项目使用中");
   }
   return result;
  }
  static string Kind(string relative){
   string path=relative.Replace('\\','/');
   if(Regex.IsMatch(path,@"^training/[^/]+/models/(GPT|SoVITS)/[^/]+\.(ckpt|pth)$",RegexOptions.IgnoreCase))return Weights;
   if(Regex.IsMatch(path,@"^training/[^/]+/(gpt_logs/ckpt|sovits_logs|features/logs_s2_[^/]+)/[^/]+\.(ckpt|pth)$",RegexOptions.IgnoreCase))return Checkpoints;
   if(Regex.IsMatch(path,@"^training/[^/]+/(gpt_logs|sovits_logs|features/logs_s2_[^/]+)/",RegexOptions.IgnoreCase))return Logs;
   if(Regex.IsMatch(path,@"^training/[^/]+/features/",RegexOptions.IgnoreCase)&&!path.Contains("/logs_s2_"))return Features;
   if(path.StartsWith("audio/",StringComparison.OrdinalIgnoreCase))return Audio;
   if(path.StartsWith("exports/",StringComparison.OrdinalIgnoreCase))return Exports;
   if(path.StartsWith("jobs/",StringComparison.OrdinalIgnoreCase))return Logs;
   return Records;
  }
  static EpochFile EpochItem(string root,string path,Project current){
   SafePath(root,path);if(!File.Exists(path))return null;int epoch=Epoch(path);if(epoch==0)return null;
   bool active=current.Trained!=null&&new[]{"sovits","gpt"}.Any(key=>current.Trained.ContainsKey(key)&&string.Equals(Full(Convert.ToString(current.Trained[key])),Full(path),StringComparison.OrdinalIgnoreCase));
   return new EpochFile(){Path=Full(path),Epoch=epoch,Bytes=new FileInfo(path).Length,Current=active};
  }
  public static FileCatalog Scan(Project current){
   string root=Full(current.Directory);if(!Directory.Exists(root)||string.Equals(root,Full(System.IO.Path.GetPathRoot(root)),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请先创建或打开一个有效的声音项目。");
   var protection=Protected(current);var catalog=new FileCatalog(){Root=root};var paths=Walk(root);
   foreach(string path in paths){string relative=path.Substring(root.Length+1);string kind=Kind(relative),reason;bool referenced=protection.TryGetValue(Full(path),out reason);var file=new FileInfo(path);catalog.Files.Add(new ManagedFile(){Path=Full(path),Relative=relative,Kind=kind,Bytes=file.Length,ModifiedTicks=file.LastWriteTimeUtc.Ticks,CanRecycle=!referenced&&kind!=Records,Reason=referenced?reason:kind==Records?"保留项目记录":""});}
   foreach(var group in catalog.Files.Where(f=>f.Kind==Weights&&Epoch(f.Path)>0).GroupBy(f=>System.IO.Path.GetDirectoryName(f.Path),StringComparer.OrdinalIgnoreCase)){var latest=group.OrderBy(f=>Epoch(f.Path)).Last();latest.Latest=true;}
   foreach(string manifestPath in paths.Where(p=>Regex.IsMatch(p.Substring(root.Length+1).Replace('\\','/'),@"^training/[^/]+/voice\.json$",RegexOptions.IgnoreCase))){
    var manifest=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(manifestPath,Encoding.UTF8));object version;if(manifest==null||!manifest.TryGetValue("version",out version)||!(Convert.ToString(version)=="v4"||Convert.ToString(version)=="v2Pro"))continue;
    string run=System.IO.Path.GetDirectoryName(manifestPath);var item=new TrainingRun(){Path=run,Version=Convert.ToString(version),Label=System.IO.Path.GetFileName(run)+" · "+ModelSelection.Name(Convert.ToString(version))};
    foreach(var pair in new[]{new[]{"sovits","SoVITS",".pth"},new[]{"gpt","GPT",".ckpt"}}){
     var candidates=paths.Where(p=>string.Equals(System.IO.Path.GetDirectoryName(p),System.IO.Path.Combine(run,"models",pair[1]),StringComparison.OrdinalIgnoreCase)&&string.Equals(System.IO.Path.GetExtension(p),pair[2],StringComparison.OrdinalIgnoreCase)).ToList();object shared;
     if(manifest.TryGetValue(pair[0],out shared)&&!string.IsNullOrWhiteSpace(Convert.ToString(shared))&&Inside(root,Convert.ToString(shared)))candidates.Add(Convert.ToString(shared));
     var models=candidates.Select(Full).Distinct(StringComparer.OrdinalIgnoreCase).Select(p=>EpochItem(root,p,current)).Where(p=>p!=null).OrderBy(p=>p.Epoch).ToList();if(pair[0]=="sovits")item.SoVITS=models;else item.GPT=models;
     var last=models.LastOrDefault();if(last!=null){var row=catalog.Files.FirstOrDefault(f=>string.Equals(f.Path,last.Path,StringComparison.OrdinalIgnoreCase));if(row!=null)row.Latest=true;}
    }
    catalog.Runs.Add(item);
   }
   catalog.Runs=catalog.Runs.OrderByDescending(r=>r.Path).ToList();return catalog;
  }
  public static Dictionary<string,object> Select(Project current,string runPath,string sovitsPath,string gptPath){
   var run=Scan(current).Runs.FirstOrDefault(r=>string.Equals(r.Path,Full(runPath),StringComparison.OrdinalIgnoreCase));if(run==null||!run.SoVITS.Any(f=>string.Equals(f.Path,Full(sovitsPath),StringComparison.OrdinalIgnoreCase))||!run.GPT.Any(f=>string.Equals(f.Path,Full(gptPath),StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("所选训练版本已不存在或不属于这次训练，请刷新后重选。");
   return new Dictionary<string,object>{{"version",run.Version},{"sovits",Full(sovitsPath)},{"gpt",Full(gptPath)},{"run",run.Path}};
  }
  static void InvalidateFeatures(Project current,List<ManagedFile> selected){
   var features=selected.Where(f=>f.Kind==Features).ToList();string cache=System.IO.Path.Combine(current.Directory,"cache");if(features.Count==0||!Directory.Exists(cache))return;
   foreach(string path in Directory.GetFiles(cache,"features_*.json")){SafePath(current.Directory,path);var data=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));object directory;if(data!=null&&data.TryGetValue("directory",out directory)&&!string.IsNullOrWhiteSpace(Convert.ToString(directory))&&features.Any(f=>Inside(Convert.ToString(directory),f.Path))){data["signature"]="invalidated-by-file-management";string temp=path+".cleanup-"+Guid.NewGuid().ToString("N")+".tmp";SafePath(current.Directory,temp);using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(Json().Serialize(data));File.Replace(temp,path,null);}}
  }
  public static CleanupResult Recycle(Project current,IEnumerable<ManagedFile> selected,Action<IList<string>> recycle){
   var requested=selected.ToList();var catalog=Scan(current);var rows=new List<ManagedFile>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);string log=System.IO.Path.Combine(current.Directory,"file-management");SafePath(catalog.Root,log);
   foreach(var previous in requested){SafePath(catalog.Root,previous.Path);if(!seen.Add(Full(previous.Path)))continue;var fresh=catalog.Files.FirstOrDefault(f=>string.Equals(f.Path,Full(previous.Path),StringComparison.OrdinalIgnoreCase));if(fresh==null||!fresh.CanRecycle)throw new InvalidDataException("文件已被项目引用或不能清理："+previous.Relative);if(fresh.Bytes!=previous.Bytes||fresh.ModifiedTicks!=previous.ModifiedTicks)throw new InvalidDataException("文件在预览后发生变化，请刷新后重选："+previous.Relative);rows.Add(fresh);}
   var latestProtection=Protected(current);if(rows.Any(f=>latestProtection.ContainsKey(f.Path)))throw new InvalidDataException("文件引用已改变，请刷新后重选。");
   InvalidateFeatures(current,rows);var result=new CleanupResult();try{if(rows.Count>0)recycle(rows.Select(r=>r.Path).ToList());}catch(Exception error){result.Error=error.Message;}
   foreach(var row in rows)if(!File.Exists(row.Path)){result.Removed++;result.Bytes+=row.Bytes;}
   Directory.CreateDirectory(log);File.WriteAllText(System.IO.Path.Combine(log,"cleanup_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_")+Guid.NewGuid().ToString("N").Substring(0,5)+".json"),Json().Serialize(new{at=DateTimeOffset.Now.ToString("o"),result=result,files=rows.Select(r=>new{path=r.Path,bytes=r.Bytes,removed=!File.Exists(r.Path)})}),new UTF8Encoding(false));return result;
  }
 }
 public static class WindowsRecycle {
  [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IShellItem {}
  [ComImport,Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IFileOperation {
   void Advise(IntPtr sink,out uint cookie);void Unadvise(uint cookie);void SetOperationFlags(uint flags);
   void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)]string message);void SetProgressDialog(IntPtr dialog);void SetProperties(IntPtr properties);void SetOwnerWindow(IntPtr owner);
   void ApplyPropertiesToItem(IntPtr item,IntPtr sink);void ApplyPropertiesToItems(IntPtr items);
   void RenameItem(IntPtr item,[MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr sink);void RenameItems(IntPtr items,[MarshalAs(UnmanagedType.LPWStr)]string name);
   void MoveItem(IntPtr item,IntPtr destination,[MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr sink);void MoveItems(IntPtr items,IntPtr destination);
   void CopyItem(IntPtr item,IntPtr destination,[MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr sink);void CopyItems(IntPtr items,IntPtr destination);
   void DeleteItem(IShellItem item,IntPtr sink);void DeleteItems(IntPtr items);
   void NewItem(IntPtr destination,uint attributes,[MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string template,IntPtr sink);
   void PerformOperations();void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)]out bool aborted);
  }
  [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]static extern void SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,out IShellItem item);
  public static void Send(IList<string> paths,IntPtr owner){
   IFileOperation operation=null;var items=new List<IShellItem>();try{
    operation=(IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
    // Recycle explicitly. The shell must warn if recycling is unavailable.
    operation.SetOperationFlags(0x00080000|0x0040|0x0010|0x4000|0x0008|0x1000|0x2000);operation.SetOwnerWindow(owner);
    foreach(string path in paths){IShellItem item;Guid iid=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out item);items.Add(item);operation.DeleteItem(item,IntPtr.Zero);}
    operation.PerformOperations();bool aborted;operation.GetAnyOperationsAborted(out aborted);if(aborted)throw new OperationCanceledException("操作已取消；已处理的文件请到系统回收站查看。");
   }finally{foreach(var item in items)Marshal.FinalReleaseComObject(item);if(operation!=null)Marshal.FinalReleaseComObject(operation);}
  }
 }
}
