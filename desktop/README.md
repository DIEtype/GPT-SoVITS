# 声线工作室 / Voice Studio

A Windows native WPF desktop interface for the existing GPT-SoVITS Windows engine package. It provides native file dialogs, drag-and-drop import, editable transcripts, training, synthesis, and portable voice presets without starting a browser or an API server. The interface is currently in Chinese. This directory is additive; the original WebUI remains available.

## 使用前提

- Windows x64，.NET Framework 4.5 或更新版本。
- 已解压的 GPT-SoVITS Windows 整合包，包含 `runtime/python.exe`、`runtime/ffmpeg.exe`、`runtime/ffprobe.exe`、`api_v2.py`、Python 依赖及相应预训练模型。
- 本版使用整合包的 `tools.portable_runtime.activate`。仅克隆源码不包含 Python 环境与模型，不能替代完整整合包。已经验证的环境为 2026-10-06 Windows 整合包。
- 训练需要可用的 NVIDIA CUDA 显卡；自动识别依赖整合包内的本地普通话 ASR 模型。

## 编译和运行

在仓库根目录使用 PowerShell 执行：

```powershell
.\desktop\build.ps1
```

构建使用 Windows 自带的 .NET Framework C# 编译器，不需要 NuGet 下载或安装 .NET SDK。输出为 `desktop/dist/声线工作室.exe`。EXE 内嵌 XAML 和 Python 适配层；无需单独复制 `worker.py`。

双击 EXE，点击左下角“设置引擎位置”，选择整合包根目录。程序也会在自身及附近目录查找引擎。请将应用放在可写目录，避免直接放入受保护的系统目录。

也可以将构建结果复制到指定整合包旁，并初始化引擎路径：

```powershell
.\desktop\deploy.ps1 -EnginePath 'D:\Apps\GPT-SoVITS' -Destination 'D:\Apps\VoiceStudio'
```

安装脚本保留已有的 `settings.json`。运行中的旧版应用应先关闭。

## 操作流程

1. **选择素材**：原生文件对话框支持多选视频或录音，也支持拖入文件。
2. **整理片段**：提取音轨、按停顿切分，逐条试听并选择保留的片段。
3. **校对文字**：本地普通话识别，双击文字进行校对。
4. **训练声音**：完成特征准备和 GPT / SoVITS 两阶段训练，支持 V2Pro 与 V4。
5. **生成配音**：选择已微调模型，或切换 V5 Turbo / V5 Dev 底模免训练合成，填写新文稿，试听并导出 WAV。

### 微调与参考录音

“微调”就是第 4 步的训练：用自己的录音和对应文字继续训练预训练模型，更新参数并保存新的 GPT / SoVITS 模型文件。第 5 步的参考录音只是生成时的输入，用于引导音色细节、语气与节奏，不会更新模型参数。

训练底模目前支持 V2Pro 和 V4，训练页会记住选择，并提供前往 V5 Dev 免训练合成的入口。作者尚未验证 V5 的少样本微调，因此不提供会误导用户的 V5 训练选项。V4 的已训练权重不能通过切换选项升级为 V5。

生成页的“不训练 · 使用 V5 底模”模式提供 V5 Turbo（4 步、CFG 0）和 V5 Dev（32 步、CFG 1.3）。Dev 更慢，面向研究和对比；选项会随项目保存。“使用已微调模型”始终加载项目原来的训练权重，此时底模选择框禁用。

V4 官方版本发布日期为 [2025-04-22](https://github.com/RVC-Boss/GPT-SoVITS/releases/tag/20250422v4)；官方更新日志的 [2026-10-05 条目](https://github.com/RVC-Boss/GPT-SoVITS/blob/main/docs/cn/Changelog_CN.md#202610)记录 V5 新版本。微调能力和发布时间是不同问题，当前支持范围以[作者说明](https://github.com/RVC-Boss/GPT-SoVITS/wiki/GPT%E2%80%90SoVITS%E2%80%90features%28Latest%E2%80%90Including%E2%80%90v5%29)为准。

训练后仍需要 3～10 秒参考录音。程序会从已保留、已校对的训练片段中自动带入合适片段及原文，也可以试听和更换。参考录音影响语气、节奏和音色细节，不需要每次重新录制或训练。

“保存音色”将推理模型、参考录音、原文及生成设置保存为 `.voicepack`；“导入音色”校验文件后复制到新的本地目录，并创建一个项目。免训练模式保存参考录音、原文与所选 V5 底模。V5 Dev 音色包使用格式 2，需要 1.3 或更新版本；原格式 1 的 Turbo 和已训练音色包仍可导入。音色包不包含基础模型、Python 环境或训练优化器状态。

## 切片与数据

### 项目文件管理（1.4）

窗口顶部的“项目文件管理”提供两个页面：

- **训练版本**：按训练记录分别选择 SoVITS 和 GPT 的 Epoch，点击“使用所选版本去配音”。两阶段的轮数可以不同；文稿、参考录音和语速继续沿用，当前 Epoch 会显示在生成页。记录了复用 SoVITS 权重的训练也可选择。
- **文件清理**：按训练权重、恢复检查点、训练特征、音频缓存、配音、运行日志与项目记录查看文件名、大小和完整路径。“勾选中间轮次”保留已记录引用的权重及每次训练最新保留的版本；其余版本可逐项选择。

清理前显示选中文件数和逻辑大小，并重新扫描引用、大小、修改时间与路径。当前项目及同一 Projects 目录下其他项目记录的模型、录音、参考音频和最近配音受保护；无法读取项目记录时停止清理。项目外文件、目录联接及符号链接不能通过本功能清理，整合包和安装压缩包也不在项目清理范围内。

选中的文件由 Windows 移入系统回收站，可以从回收站恢复；清空回收站后才真正释放空间。若 Windows 提示某个位置无法回收，可取消该操作。删除恢复检查点会失去从该断点续训的能力；清理特征时会先使相应缓存索引失效，后续训练重新计算。逻辑大小可能因硬链接而大于实际回收空间。

这里只管理文件与切换已存在的模型，不自动删除历史版本，也不改变训练保存频率。清理过程中禁用版本切换；运行训练或生成任务时不能打开文件管理。使用新版前请关闭旧版窗口，避免多个应用同时修改同一项目。

切片沿用原版 WebUI 的默认参数：`threshold=-34`、`min_length=4000`、`min_interval=300`、`hop_size=10`、`max_sil_kept=500`。不追加“超过 12 秒强制均分”“不足 1 秒丢弃”“低峰值丢弃”。原版仍按音量和停顿寻找切点，不能保证语义断句或保留所有轻声。

每次整理使用独立目录，原始素材不被修改。重新整理会替换项目当前片段列表，建议用新项目重做素材以保留旧的校对结果。已有训练模型不会自动更新。

项目、日志、配音与导入音色默认存放于应用目录上一级的 `Projects/`、`VoiceLibrary/`。配置存放于应用目录的 `settings.json`。这些本地数据不应提交到 Git。

## 验证

`VoiceStudio.cs` 内置以下开发验证入口。使用 PowerShell 的 `Start-Process -Wait` 等待桌面 EXE 退出，并检查退出码。

- `--screenshots <output-folder>`：离屏渲染五个真实 WPF 页面，不保存项目。
- `--reference-test <report.json>`：检查默认参考片段选择和用户设置保留。
- `--model-selection-test <report.json>`：检查旧项目默认值、模型选择持久化、请求路由及新旧音色包兼容。
- `--model-ui-test <test-project.json> <output-folder>`：检查实际下拉框事件、跨页面保存、已有模型保留，并渲染训练、Dev 与已微调模型页面。
- `--project-files-test <output-folder> [project.json]`：用独立的小型测试项目检查 Epoch 排序、当前/其他项目引用保护、越界、文件变化、缓存失效及部分失败。可选的实际项目只读取。
- `--project-links-test <output-folder>`：检查测试项目的 `linked` 目录联接被排除，链接内文件不能清理。
- `--project-recycle-test <output-folder>`：新建一个极小的测试文件，实际调用系统回收站并验证回收站条目增加。
- `--files-ui-test <project.json> <output-folder>`：只读渲染项目文件管理的两个页面，检查版本应用与中间轮次勾选，不清理项目文件。
- `--native-test <project.json> <report.json>`：用真实媒体检查后台进程、内嵌适配层、结果读取和页面跳转；使用测试项目。
- `--voicepack-test <trained-test-project.json> <report.json>`：检查训练音色打包、迁移、完整性校验和取消清理；使用测试模型。

切片回归测试使用真实 WAV 文件，并逐段对照整合包原版切分器的输出：

```powershell
$engine = 'D:\Apps\GPT-SoVITS'
& "$engine\runtime\python.exe" -X utf8 -B .\desktop\tests\test_slicing.py `
  --engine $engine --worker .\desktop\worker.py --output .\desktop\qa\slicing
```

已做本机端到端验证：视频提取、切片、普通话 ASR、V2Pro 两阶段训练及保存模型后的语音生成。V4 是可选路线，未将 V2Pro 的测试结果当作 V4 全流程验证。音色包已验证换目录恢复及异常文件处理。生成质量仍需结合实际素材试听。

`desktop/tests/test_models.py` 不加载权重即可检查模型路由与采样参数。V5 Dev 已使用本机 Windows 引擎和合成参考录音验证 32 步、CFG 1.3 的实际音频生成。

## 范围

当前识别和训练流程面向普通话；生成支持中、英、日、韩、粤语。本界面尚未集成人声与伴奏分离、语音转换或独立情绪控制。该模块的 MIT 许可沿用仓库根目录的 [LICENSE](../LICENSE)。
