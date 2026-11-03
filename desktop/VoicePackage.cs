using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VoiceStudio {
 public class VoiceFile {
  public string Path {get;set;} public string Sha256 {get;set;} public long Bytes {get;set;}
 }
 public class VoiceManifest {
  public int FormatVersion {get;set;} public string Name {get;set;} public string Mode {get;set;}
  public string ModelVersion {get;set;} public string ReferenceFile {get;set;} public string ReferenceText {get;set;}
  public int RefLanguage {get;set;} public int TextLanguage {get;set;} public double Speed {get;set;}
  public string CreatedAt {get;set;} public List<VoiceFile> Files {get;set;}
 }
 public static class VoicePackage {
  const long MaximumBytes=8L*1024*1024*1024;
  static JavaScriptSerializer Serializer(){return new JavaScriptSerializer(){MaxJsonLength=1024*1024};}
  static string Full(string path){return System.IO.Path.GetFullPath(path);}
  static string Digest(SHA256 hash){return BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant();}
  static string CopyAndHash(Stream input,Stream output,CancellationToken token,Action<int> progress){
   byte[] buffer=new byte[1024*1024];using(SHA256 hash=SHA256.Create()){int read;while((read=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,read);hash.TransformBlock(buffer,0,read,buffer,0);if(progress!=null)progress(read);}hash.TransformFinalBlock(new byte[0],0,0);return Digest(hash);}
  }
  public static void Export(Project project,string target,CancellationToken token,Action<double> progress){
   if(!string.Equals(System.IO.Path.GetExtension(target),".voicepack",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("音色文件请使用 .voicepack 扩展名。");
   if(!File.Exists(project.ReferencePath)||string.IsNullOrWhiteSpace(project.ReferenceText))throw new InvalidDataException("请先选定一段示例录音，并填写对应文字，再保存音色。");
   string suffix=System.IO.Path.GetExtension(project.ReferencePath).ToLowerInvariant();if(!new[]{".wav",".mp3",".m4a",".flac",".ogg",".aac"}.Contains(suffix))throw new InvalidDataException("示例录音格式不受支持，请选择 WAV、MP3、M4A、FLAC、OGG 或 AAC。");
   bool trained=project.UseTrained&&project.Trained!=null;
   string version=trained?Convert.ToString(project.Trained["version"]):ModelSelection.BaseVersion(project.QuickVersion);
   var manifest=new VoiceManifest(){FormatVersion=!trained&&version=="v5dev"?2:1,Name=System.IO.Path.GetFileNameWithoutExtension(target),Mode=trained?"trained":"reference",ModelVersion=version,ReferenceFile="reference"+suffix,ReferenceText=project.ReferenceText,RefLanguage=project.RefLanguage,TextLanguage=project.TextLanguage,Speed=project.Speed,CreatedAt=DateTimeOffset.Now.ToString("o"),Files=new List<VoiceFile>()};
   var sources=new Dictionary<string,string>{{manifest.ReferenceFile,project.ReferencePath}};
   if(trained){sources["models/voice.pth"]=Convert.ToString(project.Trained["sovits"]);sources["models/speech.ckpt"]=Convert.ToString(project.Trained["gpt"]);}
   long total=0;foreach(var pair in sources){if(!File.Exists(pair.Value))throw new FileNotFoundException("音色模型或示例录音不存在，请检查项目文件。",pair.Value);if(string.Equals(Full(pair.Value),Full(target),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("音色文件不能覆盖原模型或示例录音。");total+=new FileInfo(pair.Value).Length;}
   if(total>MaximumBytes)throw new InvalidDataException("音色内容超过 8GB，请确认保存的是推理模型。");
   string temporary=Full(target)+".partial-"+Guid.NewGuid().ToString("N");long copied=0;
   try{
    using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))using(var zip=new ZipArchive(stream,ZipArchiveMode.Create)){
     foreach(var pair in sources){token.ThrowIfCancellationRequested();var entry=zip.CreateEntry(pair.Key,CompressionLevel.Fastest);string sha;using(var input=new FileStream(pair.Value,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=entry.Open())sha=CopyAndHash(input,output,token,n=>{copied+=n;if(progress!=null)progress(100.0*copied/Math.Max(1,total));});manifest.Files.Add(new VoiceFile(){Path=pair.Key,Bytes=new FileInfo(pair.Value).Length,Sha256=sha});}
     var metadata=zip.CreateEntry("voice.json",CompressionLevel.Fastest);using(var writer=new StreamWriter(metadata.Open(),new UTF8Encoding(false)))writer.Write(Serializer().Serialize(manifest));
    }
    token.ThrowIfCancellationRequested();if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);
   }finally{if(File.Exists(temporary))File.Delete(temporary);}
  }
  public static Project Import(string package,string library,CancellationToken token,Action<double> progress){
   string root=null;
   try{
    using(var stream=new FileStream(package,FileMode.Open,FileAccess.Read,FileShare.Read))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)){
     var metadata=zip.GetEntry("voice.json");if(metadata==null||metadata.Length>1024*1024)throw new InvalidDataException("这不是受支持的声线工作室音色文件。");
     VoiceManifest manifest;using(var reader=new StreamReader(metadata.Open(),Encoding.UTF8))manifest=Serializer().Deserialize<VoiceManifest>(reader.ReadToEnd());
     if(manifest==null||!(manifest.FormatVersion==1||manifest.FormatVersion==2)||!(manifest.Mode=="trained"||manifest.Mode=="reference"))throw new InvalidDataException("音色文件版本不受支持。");
     if(manifest.Mode=="trained"&&!new[]{"v2Pro","v4"}.Contains(manifest.ModelVersion))throw new InvalidDataException("音色模型版本不受支持。");
     if(manifest.Mode=="reference"&&!new[]{"v5turbo","v5dev"}.Contains(manifest.ModelVersion))throw new InvalidDataException("音色文件中的合成底模不受支持。");
     if(manifest.RefLanguage<0||manifest.RefLanguage>4||manifest.TextLanguage<0||manifest.TextLanguage>4||string.IsNullOrWhiteSpace(manifest.ReferenceText))throw new InvalidDataException("音色文件的示例文字或语言设置不完整。");
     var references=new[]{"reference.wav","reference.mp3","reference.m4a","reference.flac","reference.ogg","reference.aac"};
     if(!references.Contains(manifest.ReferenceFile))throw new InvalidDataException("音色文件中的示例录音路径不受支持。");
     var expected=new HashSet<string>(StringComparer.Ordinal){manifest.ReferenceFile};if(manifest.Mode=="trained"){expected.Add("models/voice.pth");expected.Add("models/speech.ckpt");}
     if(manifest.Files==null||manifest.Files.Count!=expected.Count||manifest.Files.Select(f=>f.Path).Distinct(StringComparer.Ordinal).Count()!=expected.Count||manifest.Files.Any(f=>!expected.Contains(f.Path)))throw new InvalidDataException("音色文件缺少必要内容，或包含不受支持的路径。");
     if(zip.Entries.Count!=expected.Count+1||zip.Entries.Any(e=>e.FullName!="voice.json"&&!expected.Contains(e.FullName)))throw new InvalidDataException("音色文件包含不受支持的内容。");
     long total=0;foreach(VoiceFile file in manifest.Files){var entry=zip.GetEntry(file.Path);if(entry==null||entry.Length!=file.Bytes||file.Bytes<1||file.Bytes>MaximumBytes||file.Sha256==null||file.Sha256.Length!=64)throw new InvalidDataException("音色文件的完整性信息不正确。");total+=file.Bytes;}if(total>MaximumBytes)throw new InvalidDataException("音色文件超过 8GB。");
     System.IO.Directory.CreateDirectory(library);root=System.IO.Path.Combine(Full(library),"voice_"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(root);long copied=0;
     foreach(VoiceFile file in manifest.Files){token.ThrowIfCancellationRequested();string destination=System.IO.Path.Combine(root,file.Path.Replace('/',System.IO.Path.DirectorySeparatorChar));string absolute=Full(destination);if(!absolute.StartsWith(root+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("音色文件包含不安全的路径。");System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute));string sha;using(var input=zip.GetEntry(file.Path).Open())using(var output=new FileStream(absolute,FileMode.CreateNew,FileAccess.Write,FileShare.None))sha=CopyAndHash(input,output,token,n=>{copied+=n;if(progress!=null)progress(100.0*copied/Math.Max(1,total));});if(!string.Equals(sha,file.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("音色文件校验失败，文件可能已损坏。请重新选择或重新保存音色。");}
     token.ThrowIfCancellationRequested();File.WriteAllText(System.IO.Path.Combine(root,"voice.json"),Serializer().Serialize(manifest),new UTF8Encoding(false));
     Project project=new Project(){Name=manifest.Name??"导入的音色",ReferencePath=System.IO.Path.Combine(root,manifest.ReferenceFile),ReferenceText=manifest.ReferenceText,RefLanguage=manifest.RefLanguage,TextLanguage=manifest.TextLanguage,Speed=Math.Max(0.6,Math.Min(1.6,manifest.Speed)),UseTrained=manifest.Mode=="trained",QuickVersion=manifest.Mode=="reference"?manifest.ModelVersion:"v5turbo",TrainingVersion=manifest.Mode=="trained"?manifest.ModelVersion:"v2Pro"};
     if(project.UseTrained)project.Trained=new Dictionary<string,object>{{"version",manifest.ModelVersion},{"sovits",System.IO.Path.Combine(root,"models","voice.pth")},{"gpt",System.IO.Path.Combine(root,"models","speech.ckpt")},{"run",root}};
     return project;
    }
   }catch{if(root!=null&&System.IO.Directory.Exists(root)){string validRoot=Full(library)+System.IO.Path.DirectorySeparatorChar;if(!Full(root).StartsWith(validRoot,StringComparison.OrdinalIgnoreCase))throw;System.IO.Directory.Delete(root,true);}throw;}
  }
 }
}
