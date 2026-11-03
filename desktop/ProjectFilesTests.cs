using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace VoiceStudio {
 public static class ProjectFilesTests {
  static JavaScriptSerializer Json(){return new JavaScriptSerializer(){MaxJsonLength=32*1024*1024};}
  static void Save(string path,object value){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,Json().Serialize(value),new UTF8Encoding(false));}
  static string Put(string root,string relative){string path=ProjectFiles.Full(Path.Combine(root,relative));Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,new byte[]{1,2,3,4,5});return path;}
  static bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
  static string Hash(string path){using(var sha=SHA256.Create())using(var file=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(file));}
  public static void Run(string folder,string actualProject){
   Directory.CreateDirectory(folder);string fixture=Path.Combine(Path.GetFullPath(folder),"fixture_"+Guid.NewGuid().ToString("N")),root=Path.Combine(fixture,"project-a"),other=Path.Combine(fixture,"project-b");Directory.CreateDirectory(root);Directory.CreateDirectory(other);var checks=new Dictionary<string,bool>();
   string run=Path.Combine(root,"training","voice_20260101_010101_test");string s1=Put(run,"models/SoVITS/test_e1_s1.pth"),s2=Put(run,"models/SoVITS/test_e2_s2.pth"),g1=Put(run,"models/GPT/test-e1.ckpt"),g2=Put(run,"models/GPT/test-e2.ckpt"),g10=Put(run,"models/GPT/test-e10.ckpt");
   var trained=new Dictionary<string,object>{{"version","v4"},{"sovits",s2},{"gpt",g10},{"run",run}};Save(Path.Combine(run,"voice.json"),trained);
   string second=Path.Combine(root,"training","voice_20260102_020202_cached");string cg1=Put(second,"models/GPT/cached-e1.ckpt"),cg2=Put(second,"models/GPT/cached-e2.ckpt");Save(Path.Combine(second,"voice.json"),new Dictionary<string,object>{{"version","v4"},{"sovits",s2},{"gpt",cg2},{"run",second}});
   string audio=Put(root,"audio/current.wav"),oldAudio=Put(root,"audio/unused.wav"),latest=Put(root,"exports/latest.wav"),older=Put(root,"exports/older.wav"),source=Put(fixture,"outside/original.wav");string feature=Put(run,"features/3-bert/sample.pt");Put(run,"features/logs_s2_v4_lora_32/G_latest.pth");Put(run,"gpt_logs/ckpt/epoch=9-step=10.ckpt");Put(root,"jobs/previous.log");
   Project project=new Project(){Name="文件管理测试",Directory=root,Trained=trained,UseTrained=true,ReferencePath=audio,ReferenceText="参考文字",LastOutput=latest,Sources=new List<string>{source},Clips=new List<Clip>{new Clip(){Path=audio,Duration=4,Text="测试",Use=false}}};string projectFile=Path.Combine(root,"project.json");Save(projectFile,project);
   Project sibling=new Project(){Directory=other,Sources=new List<string>{g1}};string siblingFile=Path.Combine(other,"project.json");Save(siblingFile,sibling);
   string cache=Path.Combine(root,"cache/features_v4.json");Save(cache,new Dictionary<string,object>{{"signature","existing"},{"directory",Path.Combine(run,"features")}});
   FileCatalog catalog=ProjectFiles.Scan(project);Func<string,ManagedFile> row=path=>ProjectFiles.Scan(project).Files.Single(f=>f.Path==path);
   checks["epochs_are_sorted_numerically"]=catalog.Runs.Single(r=>r.Path==run).GPT.Select(f=>f.Epoch).SequenceEqual(new[]{1,2,10});
   checks["cached_sovits_is_available"]=catalog.Runs.Single(r=>r.Path==second).SoVITS.Any(f=>f.Path==s2);
   checks["active_models_and_audio_are_locked"]=new[]{s2,g10,audio,latest}.All(p=>!row(p).CanRecycle);
   checks["other_project_references_are_locked"]=!row(g1).CanRecycle&&row(g1).Reason.Contains("其他");
   checks["records_locked_and_checkpoints_separate"]=!row(projectFile).CanRecycle&&catalog.Files.Count(f=>f.Kind==ProjectFiles.Checkpoints)==2;
   checks["latest_is_identified"]=row(cg2).Latest&&!row(cg1).Latest;
   var selected=ProjectFiles.Select(project,run,s1,g2);checks["selected_epoch_pair_preserves_version"]=(string)selected["version"]=="v4"&&(string)selected["sovits"]==s1&&(string)selected["gpt"]==g2;
   int callbacks=0;Action<IList<string>> untouched=paths=>{callbacks++;};
   checks["protected_file_rejected_before_callback"]=Reject(()=>ProjectFiles.Recycle(project,new[]{row(s2)},untouched))&&callbacks==0;
   checks["outside_path_rejected_before_callback"]=Reject(()=>ProjectFiles.Recycle(project,new[]{new ManagedFile(){Path=source,CanRecycle=true}},untouched))&&callbacks==0&&File.Exists(source);
   var preview=row(g2);project.Trained["gpt"]=g2;checks["new_reference_after_preview_is_protected"]=Reject(()=>ProjectFiles.Recycle(project,new[]{preview},untouched))&&callbacks==0;project.Trained["gpt"]=g10;
   preview=row(g2);File.AppendAllText(g2,"changed");checks["modified_file_requires_refresh"]=Reject(()=>ProjectFiles.Recycle(project,new[]{preview},untouched))&&callbacks==0;
   string savedSibling=File.ReadAllText(siblingFile);File.WriteAllText(siblingFile,"{ broken");checks["unreadable_project_blocks_cleanup"]=Reject(()=>ProjectFiles.Recycle(project,new[]{preview},untouched))&&callbacks==0;File.WriteAllText(siblingFile,savedSibling);
   string trash=Path.Combine(fixture,"test-trash");Directory.CreateDirectory(trash);Action<IList<string>> fakeBin=paths=>{foreach(string path in paths)File.Move(path,Path.Combine(trash,Guid.NewGuid().ToString("N")+Path.GetFileName(path)));};
   var cleaned=ProjectFiles.Recycle(project,new[]{row(feature)},fakeBin);var index=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(cache));checks["feature_cleanup_invalidates_cache"]=cleaned.Removed==1&&!File.Exists(feature)&&(string)index["signature"]!="existing";
   cleaned=ProjectFiles.Recycle(project,new[]{row(g2),row(older)},paths=>{File.Move(paths[0],Path.Combine(trash,"partial.ckpt"));throw new OperationCanceledException("test cancellation");});checks["partial_cleanup_is_reported"]=cleaned.Removed==1&&!string.IsNullOrEmpty(cleaned.Error)&&File.Exists(older);
   checks["unrelated_source_and_current_models_preserved"]=File.Exists(source)&&File.Exists(s2)&&File.Exists(g10)&&File.Exists(audio)&&File.Exists(latest)&&File.Exists(oldAudio);
   Save(Path.Combine(folder,"fixture_project.json"),project);Save(Path.Combine(folder,"fixture_locations.json"),new{project=projectFile,root=root,outside=source});
   object actual=null;if(!string.IsNullOrWhiteSpace(actualProject)){string before=Hash(actualProject);var current=Json().Deserialize<Project>(File.ReadAllText(actualProject,Encoding.UTF8));current.Directory=Path.GetDirectoryName(Path.GetFullPath(actualProject));var scan=ProjectFiles.Scan(current);bool protectedModels=new[]{Convert.ToString(current.Trained["sovits"]),Convert.ToString(current.Trained["gpt"])}.All(p=>scan.Files.Any(f=>string.Equals(f.Path,p,StringComparison.OrdinalIgnoreCase)&&!f.CanRecycle));checks["actual_project_read_only_and_protected"]=before==Hash(actualProject)&&protectedModels;actual=new{runs=scan.Runs.Count,files=scan.Files.Count,middle_models=scan.Files.Count(f=>f.Kind==ProjectFiles.Weights&&!f.Latest&&f.CanRecycle)};}
   bool success=checks.Values.All(v=>v);Save(Path.Combine(folder,"report.json"),new{success=success,checks=checks,actual=actual});Environment.ExitCode=success?0:1;
  }
  public static void Links(string folder){
   var location=Json().Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(folder,"fixture_locations.json")));Project project=Json().Deserialize<Project>(File.ReadAllText(location["project"]));string directory=Path.Combine(project.Directory,"linked"),linked=Path.Combine(directory,"original.wav");bool present=Directory.Exists(directory)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0;int callbacks=0;bool excluded=ProjectFiles.Scan(project).Files.All(f=>!f.Path.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));bool blocked=Reject(()=>ProjectFiles.Recycle(project,new[]{new ManagedFile(){Path=linked,CanRecycle=true}},paths=>callbacks++));bool success=present&&excluded&&blocked&&callbacks==0&&File.Exists(location["outside"]);Save(Path.Combine(folder,"link_report.json"),new{success=success,link_present=present,linked_folder_not_scanned=excluded,linked_file_not_recycled=blocked});Environment.ExitCode=success?0:1;
  }
  [StructLayout(LayoutKind.Sequential)]struct RecycleInfo {public uint Size;public long Bytes;public long Items;}
  [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHQueryRecycleBin(string root,ref RecycleInfo info);
  public static void RecycleFixture(string folder){
   string root=Path.Combine(Path.GetFullPath(folder),"recycle-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Project project=new Project(){Directory=root};Save(Path.Combine(root,"project.json"),project);string file=Put(root,"exports/voice-studio-recycle-test-"+Guid.NewGuid().ToString("N")+".txt");var row=ProjectFiles.Scan(project).Files.Single(f=>f.Path==file);RecycleInfo before=new RecycleInfo(){Size=(uint)Marshal.SizeOf(typeof(RecycleInfo))},after=before;int first=SHQueryRecycleBin(Path.GetPathRoot(root),ref before);var result=ProjectFiles.Recycle(project,new[]{row},paths=>WindowsRecycle.Send(paths,IntPtr.Zero));int last=SHQueryRecycleBin(Path.GetPathRoot(root),ref after);bool success=first==0&&last==0&&result.Removed==1&&after.Items>before.Items;Save(Path.Combine(folder,"recycle_report.json"),new{success=success,removed=result.Removed,recycle_bin_count_increased=after.Items>before.Items,error=result.Error});Environment.ExitCode=success?0:1;
  }
 }
}
