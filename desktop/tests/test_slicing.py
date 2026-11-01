"""Compare adapter output against the installed original slicer, with real WAV I/O."""
import argparse
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import wave

import numpy as np


RATE = 32000


def read_audio(path):
    with wave.open(str(path), "rb") as stream:
        assert stream.getframerate() == RATE and stream.getnchannels() == 1
        return np.frombuffer(stream.readframes(stream.getnframes()), dtype="<i2").copy()


def tone(seconds, amplitude=0.15):
    return amplitude * np.sin(np.arange(round(seconds * RATE)) * (2 * np.pi * 233 / RATE))


def write_audio(path, samples):
    with wave.open(str(path), "wb") as stream:
        stream.setnchannels(1)
        stream.setsampwidth(2)
        stream.setframerate(RATE)
        stream.writeframes(np.rint(samples * 32767).astype("<i2").tobytes())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", required=True)
    parser.add_argument("--worker", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--source")
    args = parser.parse_args()
    root = Path(args.output).resolve()
    root.mkdir(parents=True, exist_ok=True)
    spec = importlib.util.spec_from_file_location("studio_adapter", args.worker)
    adapter = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(adapter)
    sys.path.insert(0, str(Path(args.engine) / "tools"))
    from slicer2 import Slicer

    cases = {
        "continuous_15_seconds": tone(15),
        "short_600_ms": tone(0.6),
        "quiet_200_ms": tone(0.2, 0.001),
        "pauses": np.concatenate((tone(3.8), np.zeros(round(0.8 * RATE)), tone(4.6),
                                  np.zeros(round(1.6 * RATE)), tone(6.4))),
        "extremely_short_5_ms": tone(0.005),
    }
    sources = []
    for name, samples in cases.items():
        source = root / (name + ".wav")
        write_audio(source, samples)
        sources.append((name, source))
    if args.source:
        sources.append(("existing_source_read_only", Path(args.source)))

    results = {}
    for name, source in sources:
        project = root / name
        request = dict(engine=args.engine, project_dir=str(project), sources=[str(source)])
        logs = io.StringIO()
        with contextlib.redirect_stdout(logs):
            adapter.Engine(request).prepare()
        events = [json.loads(line[len(adapter.PREFIX):]) for line in logs.getvalue().splitlines()
                  if line.startswith(adapter.PREFIX)]
        result = next(item for item in reversed(events) if item["kind"] == "result")
        pcm = next(project.glob("audio/*/source_000.wav"))
        decoded = read_audio(pcm).astype(np.float32) / 32768
        original = Slicer(sr=RATE, threshold=-34, min_length=4000, min_interval=300,
                          hop_size=10, max_sil_kept=500)
        expected = [item if isinstance(item, np.ndarray) else item[0]
                    for item in original.slice(decoded)]
        actual = [read_audio(clip["Path"]) for clip in result["clips"]]
        count_matches = len(actual) == len(expected)
        lengths_match = count_matches and all(len(a) == len(b) for a, b in zip(actual, expected))
        samples_match = lengths_match and all(np.array_equal(a, (np.clip(b, -1, 1) * 32767).astype("<i2"))
                                             for a, b in zip(actual, expected))
        entry = dict(success=bool(count_matches and lengths_match and samples_match),
                     clips=len(actual), original_clips=len(expected),
                     seconds=round(sum(map(len, actual)) / RATE, 3),
                     longest_seconds=round(max(map(len, actual), default=0) / RATE, 3),
                     boundaries_match_original=bool(lengths_match),
                     samples_match_slicer_output=bool(samples_match))
        if name == "continuous_15_seconds":
            entry["success"] &= len(actual) == 1 and len(actual[0]) == 15 * RATE
        if name == "short_600_ms":
            entry["success"] &= len(actual) == 1 and len(actual[0]) == round(0.6 * RATE)
        if name == "quiet_200_ms":
            entry["success"] &= len(actual) == 1 and np.abs(actual[0]).max() > 0
        if name == "pauses":
            previous = Slicer(sr=RATE, threshold=-34, min_length=4000, min_interval=300,
                              hop_size=10, max_sil_kept=350)
            previous_lengths = [len(item[0]) for item in previous.slice(decoded)]
            entry["fixture_distinguishes_500_from_350"] = previous_lengths != list(map(len, expected))
            entry["success"] &= entry["fixture_distinguishes_500_from_350"]
        entry["success"] = bool(entry["success"])
        results[name] = entry
    report = dict(success=all(item["success"] for item in results.values()), cases=results)
    (root / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
    return 0 if report["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
