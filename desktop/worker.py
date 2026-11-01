"""Local engine adapter for Voice Studio. No web server or global environment changes."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import traceback
import uuid
import wave

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
PREFIX = "@VOICE_STUDIO:"
CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0


def event(kind, **values):
    print(PREFIX + json.dumps(dict(kind=kind, **values), ensure_ascii=False), flush=True)


def write_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def capture(args):
    result = subprocess.run([str(x) for x in args], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", "replace")[-3000:])
    return result.stdout


class Engine:
    def __init__(self, request):
        self.request = request
        self.root = Path(request["engine"]).resolve()
        self.python = self.root / "runtime/python.exe"
        self.ffmpeg = self.root / "runtime/ffmpeg.exe"
        self.ffprobe = self.root / "runtime/ffprobe.exe"
        for path in (self.python, self.ffmpeg, self.ffprobe):
            if not path.is_file():
                raise ValueError("引擎文件缺失，请在设置中重新选择完整的 GPT-SoVITS 文件夹：" + str(path))
        self.project = Path(request.get("project_dir", self.root.parent / "Projects"))
        self.project.mkdir(parents=True, exist_ok=True)
        self.pretrained = self.root / "GPT_SoVITS/pretrained_models"
        os.environ["PATH"] = str(self.root / "runtime") + os.pathsep + os.environ.get("PATH", "")
        os.environ["PYTHONIOENCODING"] = "utf-8"
        os.environ["PYTHONUTF8"] = "1"
        os.environ["language"] = "zh_CN"
        os.environ["G2PW"] = "0"
        sys.path[:0] = [str(self.root), str(self.root / "GPT_SoVITS"), str(self.root / "tools")]

    def heavy(self):
        event("progress", message="正在加载语音引擎，首次启动可能需要一会儿…", percent=-1)
        from tools.portable_runtime import activate
        activate(change_cwd=True)

    def probe(self, file):
        if not Path(file).is_file():
            raise ValueError("文件不存在，请重新选择视频或录音：" + str(file))
        try:
            data = json.loads(capture([self.ffprobe, "-v", "error", "-show_entries",
                                      "format=duration:stream=codec_type,sample_rate,channels", "-of", "json", file]))
        except Exception as error:
            raise ValueError("无法读取这个文件的音轨，请确认视频可以正常播放：" + Path(file).name) from error
        if not any(s.get("codec_type") == "audio" for s in data.get("streams", [])):
            raise ValueError("这个文件没有可读取的音轨：" + str(file))
        return float(data["format"]["duration"])

    def inspect(self):
        gpu = "未检测到 NVIDIA 显卡"
        try:
            gpu = capture(["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader"]).decode().strip()
        except Exception:
            pass
        event("result", gpu=gpu, engine=str(self.root), available=True)

    def prepare(self):
        import numpy as np
        from slicer2 import Slicer
        sources = self.request.get("sources", [])
        if not sources:
            raise ValueError("请先选择至少一个视频或录音文件。")
        # Every run gets a separate directory. Previously edited clips stay intact.
        run = self.project / "audio" / (time.strftime("%Y%m%d_%H%M%S_") + uuid.uuid4().hex[:5])
        clip_dir = run / "clips"
        clip_dir.mkdir(parents=True)
        clips = []
        for number, source in enumerate(sources):
            source = Path(source)
            duration = self.probe(source)
            event("progress", message="读取音轨：" + source.name, percent=100 * number / len(sources))
            pcm = run / ("source_%03d.wav" % number)
            # Argument lists and -nostdin keep filenames out of shell parsing.
            capture([self.ffmpeg, "-nostdin", "-v", "error", "-i", source, "-map", "0:a:0",
                     "-vn", "-ac", "1", "-ar", "32000", "-c:a", "pcm_s16le", "-y", pcm])
            with wave.open(str(pcm), "rb") as stream:
                audio = np.frombuffer(stream.readframes(stream.getnframes()), dtype="<i2").astype(np.float32) / 32768
            slicer = Slicer(sr=32000, threshold=-34, min_length=4000, min_interval=300,
                            hop_size=10, max_sil_kept=500)
            # Match the original WebUI defaults. Save the slicer's output without
            # adding duration-based splits or discarding short/quiet recordings.
            for chunk in slicer.slice(audio):
                # The upstream slicer returns a bare waveform for extremely short input.
                segment = chunk if isinstance(chunk, np.ndarray) else chunk[0]
                clip_path = clip_dir / ("clip_%04d.wav" % (len(clips) + 1))
                with wave.open(str(clip_path), "wb") as out:
                    out.setnchannels(1)
                    out.setsampwidth(2)
                    out.setframerate(32000)
                    out.writeframes((np.clip(segment, -1, 1) * 32767).astype("<i2").tobytes())
                clips.append(dict(Use=True, Name=clip_path.name, Path=str(clip_path),
                                  Duration=round(len(segment) / 32000, 2), Text=""))
            event("progress", message="已整理 " + source.name, percent=100 * (number + 1) / len(sources))
        if not clips:
            raise ValueError("没有找到足够清晰的语音片段，请换一段有人说话的素材。")
        result = dict(clips=clips, duration=round(sum(c["Duration"] for c in clips), 2))
        write_json(run / "clips.json", result)
        event("result", **result)

    def transcribe(self):
        self.heavy()
        import torch
        from funasr import AutoModel
        clips = self.request.get("clips", [])
        if not clips:
            raise ValueError("请先整理录音片段。")
        models = self.root / "tools/asr/models"
        asr = models / "speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-pytorch"
        if not (asr / "model.pt").exists():
            raise ValueError("普通话识别模型缺失，请补全整合包中的 tools/asr/models。文字也可以手动填写。")
        event("progress", message="加载普通话识别模型，首次加载可能较慢…", percent=-1)
        kwargs = dict(model=str(asr), device="cuda:0" if torch.cuda.is_available() else "cpu",
                      disable_update=True, disable_pbar=True)
        punc = next(iter(models.glob("*punc_ct-transformer*")), None)
        if punc:
            kwargs["punc_model"] = str(punc)
        model = AutoModel(**kwargs)
        count = len([c for c in clips if c.get("Use", True)])
        done = 0
        for clip in clips:
            if not clip.get("Use", True):
                continue
            result = model.generate(input=clip["Path"], batch_size_s=60)
            text = re.sub(r"<\|[^>]+\|>", "", result[0].get("text", "")).strip()
            if not text:
                event("log", message="未识别出文字：" + clip["Name"] + "，请试听并手动填写或取消保留。")
            clip["Text"] = text
            done += 1
            event("clip", path=clip["Path"], text=text)
            event("progress", message="已识别 %s / %s 条" % (done, count), percent=100 * done / max(count, 1))
        event("result", clips=clips)

    def script(self, relative, args=(), env=None, message="处理中…"):
        event("progress", message=message, percent=-1)
        bootstrap = ("import runpy,sys;sys.path[:0]=[sys.argv[1],sys.argv[1]+'/GPT_SoVITS'];"
                     "from tools.portable_runtime import activate;activate(change_cwd=True);"
                     "sys.argv=sys.argv[2:];runpy.run_path(sys.argv[0],run_name='__main__')")
        command = [str(self.python), "-X", "utf8", "-u", "-s", "-c", bootstrap, str(self.root), str(self.root / relative), *map(str, args)]
        child_env = os.environ.copy()
        child_env.update(env or {})
        child_env["NO_PROXY"] = "127.0.0.1,localhost"
        proc = subprocess.Popen(command, cwd=self.root, env=child_env, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, encoding="utf-8", errors="replace",
                                creationflags=CREATE_NO_WINDOW, bufsize=1)
        tail = []
        current_epoch = ""
        for line in proc.stdout:
            clean = re.sub(r"\x1b\[[0-9;]*[A-Za-z]", "", line).strip()
            if clean:
                print(clean, flush=True)
                tail = (tail + [clean])[-12:]
                match = re.search(r"(?:====> Epoch:|Epoch\s*:?)\s*(\d+)", clean)
                if match:
                    current_epoch = str(int(match.group(1)) + (1 if relative.endswith("s1_train.py") else 0))
                    event("progress", message=message + " · 轮次 " + current_epoch, percent=-1)
                batch_progress = re.search(r"(\d{1,3})%\|.*?\|\s*(\d+)/(\d+)", clean)
                if batch_progress and current_epoch:
                    percent, done, total = batch_progress.groups()
                    label = message + (" · 第 " + current_epoch + " 轮" if current_epoch else "")
                    event("progress", message=label + " · %s / %s 批" % (done, total), percent=int(percent))
        if proc.wait() != 0:
            details = "\n".join(tail)
            if "out of memory" in details.lower():
                raise ValueError("显存不足。请关闭其他占用显卡的软件，或将训练批次大小改为 2 后重试。")
            raise RuntimeError(message + "未完成。具体原因已保存在详细运行日志中。")

    def train(self):
        self.heavy()
        import torch
        import yaml
        if not torch.cuda.is_available():
            raise ValueError("训练需要可用的 NVIDIA 显卡。请先关闭其他占用显卡的软件，并检查驱动。")
        clips = [c for c in self.request.get("clips", []) if c.get("Use", True)]
        if len(clips) < 2:
            raise ValueError("请至少保留两条录音片段。")
        if any(not c.get("Text", "").strip() for c in clips):
            raise ValueError("保留的录音中仍有空白文字。请先完成识别和校对。")
        version = self.request.get("version", "v2Pro")
        if version not in ("v2Pro", "v4"):
            raise ValueError("训练版本请选择 V2Pro 或 V4。")
        from config import pretrained_sovits_name, pretrained_gpt_name
        base_sovits = self.root / pretrained_sovits_name[version]
        base_gpt = self.root / pretrained_gpt_name[version]
        if not base_sovits.exists() or not base_gpt.exists():
            raise ValueError("选定版本的预训练模型不完整，请检查整合包。")
        run_id = time.strftime("voice_%Y%m%d_%H%M%S_") + uuid.uuid4().hex[:5]
        run = self.project / "training" / run_id
        feature_dir = run / "features"
        feature_dir.mkdir(parents=True)
        labels = run / "dataset.list"
        labels.write_text("\n".join("%s|%s|ZH|%s" % (c["Path"], run_id, c["Text"].replace("\n", " ").replace("|", "，")) for c in clips) + "\n", encoding="utf-8")
        env = dict(inp_text=str(labels), inp_wav_dir="", exp_name=run_id, opt_dir=str(feature_dir),
                   i_part="0", all_parts="1", _CUDA_VISIBLE_DEVICES="0", is_half="True", version=version,
                   bert_pretrained_dir=str(self.pretrained / "chinese-roberta-wwm-ext-large"),
                   cnhubert_base_dir=str(self.pretrained / "chinese-hubert-base"),
                   sv_path=str(self.pretrained / "sv/pretrained_eres2netv2w24s4ep4.ckpt"),
                   pretrained_s2G=str(base_sovits), s2config_path="GPT_SoVITS/configs/s2v2Pro.json" if version == "v2Pro" else "GPT_SoVITS/configs/s2.json")
        signature_data = [(c["Path"], c["Text"], Path(c["Path"]).stat().st_size, Path(c["Path"]).stat().st_mtime_ns) for c in clips]
        signature = hashlib.sha256(json.dumps([version, signature_data], ensure_ascii=True).encode()).hexdigest()
        cache_file = self.project / "cache" / ("features_" + version + ".json")
        cached = None
        if cache_file.exists():
            saved = json.loads(cache_file.read_text(encoding="utf-8"))
            candidate = Path(saved.get("directory", ""))
            required = ("2-name2text.txt", "6-name2semantic.tsv", "3-bert", "4-cnhubert", "5-wav32k") + (("7-sv_cn",) if version == "v2Pro" else ())
            if saved.get("signature") == signature and all((candidate / x).exists() for x in required):
                cached = candidate
        if cached:
            # Keep each training run's checkpoints separate. Only immutable feature
            # files are linked into the new run; the original cache is not trained in.
            for name in required:
                source = cached / name
                paths = list(source.rglob("*")) if source.is_dir() else [source]
                if source.is_dir():
                    (feature_dir / name).mkdir(parents=True, exist_ok=True)
                for path in paths:
                    if path.is_dir():
                        continue
                    destination = feature_dir / path.relative_to(cached)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    try:
                        os.link(path, destination)
                    except OSError:
                        shutil.copy2(path, destination)
            event("progress", message="素材未变化，复用已准备的训练特征…", percent=-1)
        else:
            self.script("GPT_SoVITS/prepare_datasets/1-get-text.py", env=env, message="准备文字特征（1/4）")
            split_text = feature_dir / "2-name2text-0.txt"
            if not split_text.exists() or not split_text.read_text(encoding="utf-8").strip():
                raise ValueError("文字特征为空，请检查录音文字是否正确。")
            (feature_dir / "2-name2text.txt").write_text(split_text.read_text(encoding="utf-8"), encoding="utf-8")
            self.script("GPT_SoVITS/prepare_datasets/2-get-hubert-wav32k.py", env=env, message="准备声音特征（2/4）")
            if version == "v2Pro":
                self.script("GPT_SoVITS/prepare_datasets/2-get-sv.py", env=env, message="准备音色特征（3/4）")
            self.script("GPT_SoVITS/prepare_datasets/3-get-semantic.py", env=env, message="准备语义特征（4/4）")
            semantic = (feature_dir / "6-name2semantic-0.tsv").read_text(encoding="utf-8").strip()
            if not semantic:
                raise ValueError("语义特征为空，请检查录音是否能正常播放。")
            (feature_dir / "6-name2semantic.tsv").write_text("item_name\tsemantic_audio\n" + semantic + "\n", encoding="utf-8")
            write_json(cache_file, dict(signature=signature, directory=str(feature_dir)))
        sovits_out, gpt_out = run / "models/SoVITS", run / "models/GPT"
        sovits_out.mkdir(parents=True)
        gpt_out.mkdir(parents=True)
        checkpoint_folder = "logs_s2_v2Pro" if version == "v2Pro" else "logs_s2_v4_lora_32"
        (feature_dir / checkpoint_folder).mkdir(parents=True, exist_ok=True)
        batch = max(1, min(8, int(self.request.get("batch", 4))))
        s2 = json.loads((self.root / env["s2config_path"]).read_text(encoding="utf-8"))
        s2["train"].update(batch_size=batch, epochs=int(self.request.get("sovits_epochs", 8)),
                           text_low_lr_rate=0.4, pretrained_s2G=str(base_sovits),
                           pretrained_s2D=str(base_sovits).replace("s2G", "s2D"), if_save_latest=True,
                           if_save_every_weights=True, save_every_epoch=1, gpu_numbers="0", grad_ckpt=True, lora_rank=32)
        s2["model"]["version"] = version
        s2["data"]["exp_dir"] = str(feature_dir)
        s2["s2_ckpt_dir"] = str(run / "sovits_logs")
        s2.update(save_weight_dir=str(sovits_out), name=run_id, version=version)
        s2_path = run / "sovits.json"
        s2_path.write_text(json.dumps(s2, ensure_ascii=True, indent=2), encoding="ascii")
        sovits_cache = self.project / "cache" / ("sovits_" + version + ".json")
        sovits_key = hashlib.sha256(json.dumps([signature, s2["train"]["epochs"], batch]).encode()).hexdigest()
        sovits = None
        if sovits_cache.exists():
            saved = json.loads(sovits_cache.read_text(encoding="utf-8"))
            if saved.get("key") == sovits_key and Path(saved.get("sovits", "")).is_file():
                sovits = Path(saved["sovits"])
        if sovits:
            event("progress", message="音色模型已准备，继续训练语音表达…", percent=-1)
        else:
            self.script("GPT_SoVITS/s2_train.py" if version == "v2Pro" else "GPT_SoVITS/s2_train_v3_lora.py",
                        ["--config", s2_path], env=env, message="训练音色")
            sovits = max(sovits_out.glob("*.pth"), key=lambda p: p.stat().st_mtime, default=None)
            if sovits is None:
                raise ValueError("音色训练结束，但没有找到声音模型，请展开详细日志查看原因。")
            write_json(sovits_cache, dict(key=sovits_key, sovits=str(sovits)))
        s1 = yaml.safe_load((self.root / "GPT_SoVITS/configs/s1longer-v2.yaml").read_text(encoding="utf-8"))
        s1["train"].update(batch_size=batch, epochs=int(self.request.get("gpt_epochs", 15)),
                           save_every_n_epoch=1, if_save_every_weights=True, if_save_latest=True,
                           if_dpo=False, half_weights_save_dir=str(gpt_out), exp_name=run_id)
        s1["data"]["num_workers"] = 2
        s1.update(pretrained_s1=str(base_gpt), train_semantic_path=str(feature_dir / "6-name2semantic.tsv"),
                  train_phoneme_path=str(feature_dir / "2-name2text.txt"), output_dir=str(run / "gpt_logs"))
        s1_path = run / "gpt.yaml"
        s1_path.write_text(yaml.safe_dump(s1, allow_unicode=False), encoding="utf-8")
        env["hz"] = "25hz"
        self.script("GPT_SoVITS/s1_train.py", ["--config_file", s1_path], env=env, message="训练语音表达")
        gpt = max(gpt_out.glob("*.ckpt"), key=lambda p: p.stat().st_mtime, default=None)
        if sovits is None or gpt is None:
            raise ValueError("训练进程结束，但未找到完整的声音模型。请展开详细日志检查。")
        trained = dict(version=version, sovits=str(sovits), gpt=str(gpt), run=str(run))
        write_json(run / "voice.json", trained)
        event("result", trained=trained)

    def synthesize(self):
        reference = Path(self.request.get("reference", ""))
        if not reference.is_file():
            raise ValueError("请选择一段参考录音。")
        duration = self.probe(reference)
        if not 3 <= duration <= 10:
            raise ValueError("参考录音需要在 3～10 秒之间，当前为 %.1f 秒。请从录音片段中选一条合适的。" % duration)
        if not self.request.get("reference_text", "").strip():
            raise ValueError("请填写参考录音中实际说出的文字。")
        if not self.request.get("text", "").strip():
            raise ValueError("请填写需要生成的配音文字。")
        self.heavy()
        import numpy as np
        import soundfile as sf
        import torch
        from TTS_infer_pack.TTS import TTS, TTS_Config
        from config import pretrained_sovits_name, pretrained_gpt_name
        trained = self.request.get("trained") if self.request.get("use_trained") else None
        version = trained["version"] if trained else self.request.get("quick_version", "v5turbo")
        custom = dict(device="cuda" if torch.cuda.is_available() else "cpu", is_half=torch.cuda.is_available(),
                      version=version, t2s_weights_path=trained["gpt"] if trained else str(self.root / pretrained_gpt_name[version]),
                      vits_weights_path=trained["sovits"] if trained else str(self.root / pretrained_sovits_name[version]),
                      cnhuhbert_base_path=str(self.pretrained / "chinese-hubert-base"),
                      bert_base_path=str(self.pretrained / "chinese-roberta-wwm-ext-large"))
        for key in ("t2s_weights_path", "vits_weights_path"):
            if not Path(custom[key]).is_file():
                raise ValueError("声音模型文件不存在：" + custom[key])
        event("progress", message="正在加载声音模型…", percent=-1)
        config = TTS_Config({"custom": custom})
        # Keep desktop settings separate from the author's WebUI configuration.
        config.configs_path = str(self.project / "tts_infer.yaml")
        tts = TTS(config)
        event("progress", message="正在生成配音…", percent=-1)
        inputs = dict(text=self.request["text"], text_lang=self.request.get("text_lang", "zh"),
                      ref_audio_path=str(reference), prompt_text=self.request["reference_text"],
                      prompt_lang=self.request.get("ref_lang", "zh"), speed_factor=float(self.request.get("speed", 1)),
                      text_split_method="cut5", batch_size=1, seed=42, sample_steps=4 if version == "v5turbo" else 8,
                      use_cuda_graph=False, use_flash_attention=False, parallel_infer=True)
        pieces, rate = [], None
        for rate, audio in tts.run(inputs):
            pieces.append(audio)
        if not pieces:
            raise RuntimeError("没有生成音频，请检查输入文字和参考录音。")
        output = self.project / "exports" / (time.strftime("配音_%Y%m%d_%H%M%S_") + uuid.uuid4().hex[:4] + ".wav")
        output.parent.mkdir(parents=True, exist_ok=True)
        sf.write(str(output), np.concatenate(pieces), rate)
        event("result", output=str(output), duration=sf.info(output).duration)


def main():
    request = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
    engine = Engine(request)
    action = request["action"]
    if action not in ("inspect", "prepare", "transcribe", "train", "synthesize"):
        raise ValueError("未知任务。")
    getattr(engine, action)()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        traceback.print_exc()
        message = str(error)
        if "out of memory" in message.lower():
            message = "显存不足。请关闭其他占用显卡的软件，或将训练批次大小改为 2 后重试。"
        event("error", message=message)
        sys.exit(1)
